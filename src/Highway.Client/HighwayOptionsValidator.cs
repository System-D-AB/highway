using System.Text;

namespace Highway.Client;

/// <summary>
/// Validates <see cref="HighwayOptions"/> at startup so misconfiguration fails
/// fast, before any traffic. The NodeName rules mirror the server's identifier
/// rules (feature 004.1, Requirement 3) — NodeName is the client half of that
/// safety contract: the server rejects control characters and over-length
/// identifiers, so an invalid NodeName must fail locally at startup.
/// </summary>
internal static class HighwayOptionsValidator
{
    /// <summary>Server-side identifier length cap (004.1 MaxIdentifierBytes default).</summary>
    public const int MaxNodeNameBytes = 256;

    /// <summary>Server-side HW.RECEIVE COUNT cap (004 ReceiveMaxCount default).</summary>
    public const int MaxReceiveBatchSize = 500;

    public static void Validate(HighwayOptions options)
    {
        ValidateNodeName(options.NodeName);

        if (options.CallTimeout <= TimeSpan.Zero)
            throw new InvalidOperationException(
                $"HighwayOptions.CallTimeout must be positive, but was {options.CallTimeout}.");

        if (options.WorkerConcurrency < 1)
            throw new InvalidOperationException(
                $"HighwayOptions.WorkerConcurrency must be at least 1, but was {options.WorkerConcurrency}.");

        if (options.ReceiveBatchSize < 1 || options.ReceiveBatchSize > MaxReceiveBatchSize)
            throw new InvalidOperationException(
                $"HighwayOptions.ReceiveBatchSize must be between 1 and {MaxReceiveBatchSize}, but was {options.ReceiveBatchSize}.");

        if (options.BackstopInterval < TimeSpan.FromMilliseconds(50))
            throw new InvalidOperationException(
                $"HighwayOptions.BackstopInterval must be at least 50ms, but was {options.BackstopInterval}.");

        if (options.DrainTimeout <= TimeSpan.Zero)
            throw new InvalidOperationException(
                $"HighwayOptions.DrainTimeout must be positive, but was {options.DrainTimeout}.");

        if (options.HeartbeatInterval <= TimeSpan.Zero)
            throw new InvalidOperationException(
                $"HighwayOptions.HeartbeatInterval must be positive, but was {options.HeartbeatInterval}.");

        if (options.DiscoveryCacheTtl < TimeSpan.Zero)
            throw new InvalidOperationException(
                $"HighwayOptions.DiscoveryCacheTtl cannot be negative, but was {options.DiscoveryCacheTtl}.");
    }

    /// <summary>
    /// Validates a node name against the server's identifier rules: non-empty,
    /// at most 256 bytes, no character below U+0020, no U+007F.
    /// </summary>
    public static void ValidateNodeName(string? nodeName)
    {
        if (string.IsNullOrWhiteSpace(nodeName))
            throw new InvalidOperationException(
                "HighwayOptions.NodeName must not be blank.");

        var bytes = Encoding.UTF8.GetBytes(nodeName);

        if (bytes.Length > MaxNodeNameBytes)
            throw new InvalidOperationException(
                $"HighwayOptions.NodeName '{nodeName}' is {bytes.Length} bytes; the server's identifier cap is {MaxNodeNameBytes} bytes.");

        foreach (var b in bytes)
        {
            if (b < 0x20 || b == 0x7F)
                throw new InvalidOperationException(
                    $"HighwayOptions.NodeName '{nodeName}' contains a control character; the server rejects such identifiers.");
            if (b == (byte)'@')
                throw new InvalidOperationException(
                    $"HighwayOptions.NodeName '{nodeName}' contains '@' which is reserved for internal group-queue routing (feature 018).");
        }
    }
}
