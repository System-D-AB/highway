using System.Net;

namespace Highway.Server.Dashboard;

/// <summary>
/// Configuration for the embedded Highway dashboard.
/// </summary>
public sealed class DashboardOptions
{
    /// <summary>Whether the dashboard is active. Default: false (explicitly opt-in).</summary>
    public bool Enabled { get; set; }

    /// <summary>HTTP port for the dashboard. Default: 7500.</summary>
    public int Port { get; set; } = 7500;

    /// <summary>
    /// Network interface to listen on. Default: loopback only (secure by default).
    /// Use IPAddress.Any to expose on all interfaces.
    /// </summary>
    public IPAddress Bind { get; set; } = IPAddress.Loopback;

    /// <summary>
    /// Path prefix for reverse-proxy mounting. E.g., "/highway".
    /// Empty string means root. Must start with "/" when non-empty.
    /// </summary>
    public string PathBase { get; set; } = "";

    /// <summary>
    /// Optional API key. When set, all requests must present it.
    /// When null, no authentication is required.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>Maximum concurrent SSE streams. Default: 4.</summary>
    public int MaxConcurrentStreams { get; set; } = 4;

    /// <summary>Events buffered per stream before drops. Default: 512.</summary>
    public int StreamBufferCapacity { get; set; } = 512;

    /// <summary>SSE keep-alive interval. Default: 15 seconds.</summary>
    public TimeSpan KeepAliveInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Validates options, throwing on invalid values.</summary>
    public void Validate()
    {
        if (Port is < 1 or > 65535)
            throw new ArgumentException($"Dashboard port {Port} is out of range (1-65535).", nameof(Port));
        if (MaxConcurrentStreams < 1)
            throw new ArgumentException($"MaxConcurrentStreams must be >= 1, got {MaxConcurrentStreams}.", nameof(MaxConcurrentStreams));
        if (StreamBufferCapacity < 1)
            throw new ArgumentException($"StreamBufferCapacity must be >= 1, got {StreamBufferCapacity}.", nameof(StreamBufferCapacity));
        if (KeepAliveInterval <= TimeSpan.Zero)
            throw new ArgumentException($"KeepAliveInterval must be positive, got {KeepAliveInterval}.", nameof(KeepAliveInterval));
        if (!string.IsNullOrEmpty(PathBase) && !PathBase.StartsWith('/'))
            throw new ArgumentException($"PathBase must start with '/' when non-empty, got '{PathBase}'.", nameof(PathBase));
        if (ApiKey is not null && string.IsNullOrWhiteSpace(ApiKey))
            throw new ArgumentException("ApiKey must be non-whitespace when set.", nameof(ApiKey));
    }
}
