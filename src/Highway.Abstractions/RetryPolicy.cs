using System.Globalization;

namespace Highway.Abstractions;

/// <summary>
/// Strategy for calculating delays between retry attempts (feature 073).
/// </summary>
public enum BackoffKind : byte
{
    None = 0,
    Fixed = 1,
    Exponential = 2
}

/// <summary>
/// Parsed and normalized retry policy (feature 073, R7).
/// </summary>
public readonly record struct RetryPolicy(
    int MaxAttempts,
    BackoffKind BackoffKind,
    TimeSpan MinBackoff,
    TimeSpan MaxBackoff,
    bool Jitter,
    int MaxRequeues)
{
    public static RetryPolicy None { get; } = new(0, BackoffKind.None, TimeSpan.Zero, TimeSpan.Zero, false, 3);

    /// <summary>
    /// Computes the backoff delay in ticks for the specified message ID and attempt index (1-indexed).
    /// Uses deterministic hashing for jitter to ensure identical replay across WAL and replicas (037 R5).
    /// </summary>
    public long CalculateBackoffTicks(string messageId, int attempt)
    {
        if (BackoffKind == BackoffKind.None || attempt <= 0)
            return 0;

        long baseTicks;
        if (BackoffKind == BackoffKind.Fixed)
        {
            baseTicks = MinBackoff.Ticks;
        }
        else // Exponential
        {
            if (attempt <= 1)
            {
                baseTicks = MinBackoff.Ticks;
            }
            else if (attempt >= 62)
            {
                baseTicks = MaxBackoff.Ticks;
            }
            else
            {
                double factor = Math.Pow(2, attempt - 1);
                double computed = MinBackoff.Ticks * factor;
                baseTicks = computed >= MaxBackoff.Ticks ? MaxBackoff.Ticks : (long)computed;
            }
        }

        if (!Jitter || baseTicks <= 0)
            return baseTicks;

        // Deterministic integer jitter: ±20% based on FNV-1a hash of (messageId, attempt)
        ulong hash = 14695981039346656037UL;
        foreach (var c in messageId)
        {
            hash ^= (byte)c;
            hash *= 1099511628211UL;
            hash ^= (byte)(c >> 8);
            hash *= 1099511628211UL;
        }
        hash ^= (ulong)attempt;
        hash *= 1099511628211UL;

        // Map hash to -2000 .. +2000 (meaning -20.00% to +20.00%)
        long jitterPercentTimes100 = (long)(hash % 4001) - 2000;
        long jittered = baseTicks + (baseTicks * jitterPercentTimes100) / 10000;
        return jittered < 0 ? 0 : jittered;
    }

    /// <summary>
    /// Parses a duration string like "500ms", "30s", "5m", "2h", "1d", or "00:00:30".
    /// </summary>
    public static TimeSpan ParseDuration(string raw)
    {
        var s = raw.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(s))
            throw new FormatException("Duration cannot be empty.");

        if (s.EndsWith("ms"))
        {
            if (double.TryParse(s[..^2], NumberStyles.Float, CultureInfo.InvariantCulture, out var ms) && ms > 0)
                return TimeSpan.FromMilliseconds(ms);
        }
        else if (s.EndsWith("s"))
        {
            if (double.TryParse(s[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var sec) && sec > 0)
                return TimeSpan.FromSeconds(sec);
        }
        else if (s.EndsWith("m"))
        {
            if (double.TryParse(s[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var min) && min > 0)
                return TimeSpan.FromMinutes(min);
        }
        else if (s.EndsWith("h"))
        {
            if (double.TryParse(s[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var hrs) && hrs > 0)
                return TimeSpan.FromHours(hrs);
        }
        else if (s.EndsWith("d"))
        {
            if (double.TryParse(s[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var days) && days > 0)
                return TimeSpan.FromDays(days);
        }
        else if (TimeSpan.TryParse(s, CultureInfo.InvariantCulture, out var ts) && ts > TimeSpan.Zero)
        {
            return ts;
        }

        throw new FormatException($"Invalid duration '{raw}'. Use format like '500ms', '30s', '5m', '2h', or '00:01:00'.");
    }

    /// <summary>
    /// Parses a backoff string like "none", "fixed:30s", "exp:1s..5m", with optional "jitter".
    /// </summary>
    public static (BackoffKind Kind, TimeSpan Min, TimeSpan Max, bool Jitter) ParseBackoff(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Trim().Equals("none", StringComparison.OrdinalIgnoreCase))
            return (BackoffKind.None, TimeSpan.Zero, TimeSpan.Zero, false);

        var trimmed = raw.Trim();
        var jitter = false;

        // Check for jitter token anywhere in the string
        var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var main = parts[0];
        for (var i = 1; i < parts.Length; i++)
        {
            if (parts[i].Equals("jitter", StringComparison.OrdinalIgnoreCase))
                jitter = true;
            else
                throw new FormatException($"Unrecognized token '{parts[i]}' in backoff specification '{raw}'.");
        }

        if (main.StartsWith("fixed:", StringComparison.OrdinalIgnoreCase))
        {
            var durationStr = main["fixed:".Length..];
            var duration = ParseDuration(durationStr);
            return (BackoffKind.Fixed, duration, duration, jitter);
        }

        if (main.StartsWith("exp:", StringComparison.OrdinalIgnoreCase))
        {
            var rangeStr = main["exp:".Length..];
            var dotDot = rangeStr.IndexOf("..", StringComparison.Ordinal);
            if (dotDot <= 0 || dotDot >= rangeStr.Length - 2)
                throw new FormatException($"Exponential backoff '{main}' must follow format 'exp:<min>..<max>'.");

            var minStr = rangeStr[..dotDot];
            var maxStr = rangeStr[(dotDot + 2)..];
            var min = ParseDuration(minStr);
            var max = ParseDuration(maxStr);

            if (max < min)
                throw new FormatException($"Exponential max duration '{maxStr}' cannot be less than min duration '{minStr}'.");

            return (BackoffKind.Exponential, min, max, jitter);
        }

        throw new FormatException(
            $"Unrecognized backoff specification '{raw}'. Expected 'none', 'fixed:<duration>', or 'exp:<min>..<max>' with optional 'jitter'.");
    }

    /// <summary>
    /// Parses a <see cref="RetryAttribute"/> into a <see cref="RetryPolicy"/>.
    /// </summary>
    public static RetryPolicy FromAttribute(RetryAttribute attr)
    {
        ArgumentNullException.ThrowIfNull(attr);
        if (attr.MaxAttempts < 0)
            throw new FormatException("MaxAttempts cannot be negative.");
        if (attr.MaxRequeues < 0)
            throw new FormatException("MaxRequeues cannot be negative.");

        var (kind, min, max, jitter) = ParseBackoff(attr.Backoff);
        return new RetryPolicy(attr.MaxAttempts, kind, min, max, jitter, attr.MaxRequeues);
    }

    /// <summary>
    /// Validates a <see cref="RetryAttribute"/> for a contract, throwing <see cref="InvalidRetryPolicyException"/> on error.
    /// </summary>
    public static RetryPolicy Validate(RetryAttribute attr, Type contractType)
    {
        try
        {
            return FromAttribute(attr);
        }
        catch (Exception ex)
        {
            throw new InvalidRetryPolicyException(contractType, ex.Message);
        }
    }
}
