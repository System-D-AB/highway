using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using Highway.Abstractions;

namespace Highway.Client.Engine;

/// <summary>The timeout a request contract declares with <c>[Service(Timeout = "hh:mm:ss")]</c> (074, D7).</summary>
internal static class ServiceTimeouts
{
    private static readonly ConcurrentDictionary<Type, TimeSpan?> Cache = new();

    /// <summary>The contract's declared timeout, or null when it declares none.</summary>
    public static TimeSpan? For(Type requestType)
        => Cache.GetOrAdd(requestType, static t =>
            t.GetCustomAttribute<ServiceAttribute>()?.Timeout is { Length: > 0 } text && TryParse(text, out var value)
                ? value
                : null);

    /// <summary>The longest timeout a contract may declare (074). The broker clamps further, to its MaxRpcTimeout.</summary>
    public static readonly TimeSpan MaxDeclared = TimeSpan.FromDays(1);

    /// <summary>
    /// Parses a declared timeout: a positive <c>hh:mm:ss</c> <c>TimeSpan</c> of at most a day. The colon is required —
    /// <c>"30"</c> parses as thirty <i>days</i>, which is never what anyone meant.
    /// </summary>
    public static bool TryParse(string text, out TimeSpan value)
    {
        if (text.Contains(':', StringComparison.Ordinal)
            && TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out value)
            && value > TimeSpan.Zero && value <= MaxDeclared)
            return true;
        value = default;
        return false;
    }
}
