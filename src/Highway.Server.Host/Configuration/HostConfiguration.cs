using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;
using Highway.Abstractions.Observability;

namespace Highway.Server.Host.Configuration;

/// <summary>
/// The on-disk configuration model of <c>highways</c> (feature 031, design § Configuration
/// Model). Every property defaults to the corresponding code default, so a merged
/// configuration is "defaults, then the file, then the environment, then the command line".
///
/// <para>The schema binds 1:1 onto <see cref="HighwayServerOptions"/>,
/// <c>DashboardOptions</c>, <c>AuthenticationOptions</c> and <c>TlsOptions</c>: an option
/// reachable from the builder is reachable from the file, and no new option is invented
/// here. The builder remains authoritative for the deep rules (012's bind-address rule,
/// certificate loading, storage format); this class validates what a file can get wrong
/// on its own, naming the key.</para>
/// </summary>
public sealed class HostConfiguration
{
    public ServerSection Server { get; set; } = new();
    public AuthenticationSection Authentication { get; set; } = new();
    public TlsSection Tls { get; set; } = new();
    public DashboardSection Dashboard { get; set; } = new();

    /// <summary>
    /// Validates the merged configuration, throwing <see cref="ConfigurationException"/>
    /// naming the offending <c>section.key</c>.
    /// </summary>
    public void Validate()
    {
        if (Server.Port is < 1 or > 65535)
            throw new ConfigurationException($"server.port: {Server.Port} is out of range (1-65535).");

        if (!IPAddress.TryParse(Server.BindAddress, out _))
            throw new ConfigurationException(
                $"server.bindAddress: '{Server.BindAddress}' is not a valid IP address (e.g. \"127.0.0.1\" or \"0.0.0.0\").");

        if (Server.Ephemeral && Server.DataDir is not null)
            throw new ConfigurationException(
                "server.dataDir and server.ephemeral are mutually exclusive — a broker is either " +
                "durable at a path or memory-only by name. Remove one.");

        if (Server.MaxPayloadBytes < 1)
            throw new ConfigurationException($"server.maxPayloadBytes must be at least 1, but was {Server.MaxPayloadBytes}.");

        if (Server.MaxIdentifierBytes < 1)
            throw new ConfigurationException($"server.maxIdentifierBytes must be at least 1, but was {Server.MaxIdentifierBytes}.");

        if (Server.MaxDeliveryAttempts < 0)
            throw new ConfigurationException(
                $"server.maxDeliveryAttempts cannot be negative, but was {Server.MaxDeliveryAttempts}. Use 0 for unlimited retries.");

        if (Server.MaxDeadLetterEntries < 0)
            throw new ConfigurationException($"server.maxDeadLetterEntries cannot be negative, but was {Server.MaxDeadLetterEntries}.");

        if (Server.ReceiveDefaultCount < 1 || Server.ReceiveMaxCount < Server.ReceiveDefaultCount)
            throw new ConfigurationException(
                $"server.receiveDefaultCount ({Server.ReceiveDefaultCount}) and server.receiveMaxCount " +
                $"({Server.ReceiveMaxCount}) must both be at least 1, and the maximum cannot be below the default.");

        // Authentication: one mechanism, said one way (mirrors AuthenticationOptions.Validate).
        if (Authentication.Password is not null && string.IsNullOrWhiteSpace(Authentication.Password))
            throw new ConfigurationException(
                "authentication.password is set but empty or whitespace — supply a real password, " +
                "or remove it to run without authentication.");

        if (Authentication.Password is not null && Authentication.AclFile is not null)
            throw new ConfigurationException(
                "authentication.password and authentication.aclFile are mutually exclusive — the ACL " +
                "file replaces everything a plain password would configure. Set exactly one.");

        // TLS: Garnet accepts exactly one certificate source (mirrors TlsOptions.Validate).
        if (Tls.CertFile is not null && Tls.CertSubjectName is not null)
            throw new ConfigurationException(
                "tls.certFile and tls.certSubjectName are mutually exclusive — Garnet accepts exactly one.");

        if (Tls.RefreshFrequencySeconds < 0)
            throw new ConfigurationException($"tls.refreshFrequencySeconds cannot be negative, but was {Tls.RefreshFrequencySeconds}.");

        if (Dashboard.Port is < 1 or > 65535)
            throw new ConfigurationException($"dashboard.port: {Dashboard.Port} is out of range (1-65535).");

        if (!IPAddress.TryParse(Dashboard.BindAddress, out _))
            throw new ConfigurationException(
                $"dashboard.bindAddress: '{Dashboard.BindAddress}' is not a valid IP address.");

        if (!string.IsNullOrEmpty(Dashboard.PathBase) && !Dashboard.PathBase.StartsWith('/'))
            throw new ConfigurationException(
                $"dashboard.pathBase must start with '/' when non-empty, but was '{Dashboard.PathBase}'.");

        if (Dashboard.ApiKey is not null && string.IsNullOrWhiteSpace(Dashboard.ApiKey))
            throw new ConfigurationException("dashboard.apiKey is set but empty or whitespace.");

        if (Dashboard.MaxConcurrentStreams < 1)
            throw new ConfigurationException($"dashboard.maxConcurrentStreams must be at least 1, but was {Dashboard.MaxConcurrentStreams}.");

        if (Dashboard.StreamBufferCapacity < 1)
            throw new ConfigurationException($"dashboard.streamBufferCapacity must be at least 1, but was {Dashboard.StreamBufferCapacity}.");

        if (Dashboard.KeepAliveInterval <= TimeSpan.Zero)
            throw new ConfigurationException($"dashboard.keepAliveInterval must be positive, but was {Dashboard.KeepAliveInterval}.");
    }
}

/// <summary>The <c>"server"</c> section — maps onto <see cref="HighwayServerOptions"/>.</summary>
public sealed class ServerSection
{
    public int Port { get; set; } = 6500;
    public string BindAddress { get; set; } = "127.0.0.1";
    public string? DataDir { get; set; }
    public bool Ephemeral { get; set; }

    [JsonConverter(typeof(SizeJsonConverter))]
    public long AofSizeLimitBytes { get; set; } = 512L * 1024 * 1024;

    public string? AofSegmentSize { get; set; }

    [JsonConverter(typeof(SizeJsonConverter))]
    public long MaxQueueBytes { get; set; } = 1024L * 1024 * 1024;

    public TimeSpan Lease { get; set; } = TimeSpan.FromMinutes(5);
    public TimeSpan ReplySlotTtl { get; set; } = TimeSpan.FromMinutes(5);
    public int MaxPayloadBytes { get; set; } = 1 * 1024 * 1024;
    public int MaxIdentifierBytes { get; set; } = 256;
    public TimeSpan NodeExpiry { get; set; } = TimeSpan.FromSeconds(30);
    public bool PruningEnabled { get; set; } = true;
    public int MaxCatalogBytes { get; set; } = 256 * 1024;
    public TimeSpan SubscriberRetirementThreshold { get; set; } = TimeSpan.FromHours(24);
    public int MaxDeliveryAttempts { get; set; } = 5;
    public int MaxDeadLetterEntries { get; set; } = 10_000;
    public bool PubSubBackoffEnabled { get; set; }
    public bool RpcBackoffEnabled { get; set; }
    public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromMinutes(1);
    public int ReceiveDefaultCount { get; set; } = 10;
    public int ReceiveMaxCount { get; set; } = 500;
    public bool WaitForCommit { get; set; }
    public ObservabilitySection Observability { get; set; } = new();
}

/// <summary>The <c>"server.observability"</c> section — maps onto <c>ObservabilityOptions</c>.</summary>
public sealed class ObservabilitySection
{
    public bool RecorderEnabled { get; set; } = true;
    public int DefaultCapacity { get; set; } = 1_000;
    public TimeSpan DefaultRetention { get; set; } = TimeSpan.FromHours(1);
    public PayloadCapture DefaultCapture { get; set; } = PayloadCapture.Full;
    public long MaxBytes { get; set; } = 64L * 1024 * 1024;
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromSeconds(10);
    public bool ReplayEnabled { get; set; } = true;
    public int ReplayDefaultLimit { get; set; } = 100;
    public int ReplayMaxLimit { get; set; } = 1_000;
    public TimeSpan ReplayDefaultWindow { get; set; } = TimeSpan.FromMinutes(5);
    public bool ActivitiesEnabled { get; set; } = true;

    /// <summary>Per-name overrides, keyed by event name (e.g. <c>"orders.placed"</c>).</summary>
    public Dictionary<string, NameOverrideSection> Overrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>One per-name recorder override (feature 002).</summary>
public sealed class NameOverrideSection
{
    public int? Capacity { get; set; }
    public TimeSpan? Retention { get; set; }
    public PayloadCapture? Capture { get; set; }
}

/// <summary>The <c>"authentication"</c> section — maps onto <c>AuthenticationOptions</c> (feature 012).</summary>
public sealed class AuthenticationSection
{
    /// <summary>One shared password; the username is Garnet's <c>default</c>.</summary>
    public string? Password { get; set; }

    /// <summary>Garnet ACL file for named users — replaces the plain password entirely.</summary>
    public string? AclFile { get; set; }
}

/// <summary>The <c>"tls"</c> section — maps onto <c>TlsOptions</c> (feature 012).</summary>
public sealed class TlsSection
{
    public string? CertFile { get; set; }
    public string? CertPassword { get; set; }
    public string? CertSubjectName { get; set; }
    public bool ClientCertificateRequired { get; set; }
    public X509RevocationMode RevocationMode { get; set; } = X509RevocationMode.NoCheck;
    public string? IssuerCertificatePath { get; set; }
    public int RefreshFrequencySeconds { get; set; }
}

/// <summary>The <c>"dashboard"</c> section — maps onto <c>DashboardOptions</c>.</summary>
public sealed class DashboardSection
{
    public bool Enabled { get; set; }
    public int Port { get; set; } = 7500;
    public string BindAddress { get; set; } = "127.0.0.1";
    public string PathBase { get; set; } = "";
    public string? ApiKey { get; set; }
    public int MaxConcurrentStreams { get; set; } = 4;
    public int StreamBufferCapacity { get; set; } = 512;
    public TimeSpan KeepAliveInterval { get; set; } = TimeSpan.FromSeconds(15);
}

/// <summary>
/// Reads a size either as a JSON number (bytes) or as a string with a k/m/g suffix
/// (<c>"512m"</c>), so operators can write sizes the way they say them.
/// </summary>
internal sealed class SizeJsonConverter : JsonConverter<long>
{
    public override long Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType switch
        {
            JsonTokenType.Number => reader.GetInt64(),
            // A JsonException (not ConfigurationException) so the serializer attaches the
            // key path — the loader then names "server.aofSizeLimitBytes", not just the value.
            JsonTokenType.String => ParseSized(reader.GetString()!),
            _ => throw new JsonException("Expected a size: a number of bytes or a string like \"512m\".")
        };

    private static long ParseSized(string text)
    {
        try
        {
            return SizeFormat.Parse(text, "size value");
        }
        catch (ConfigurationException ex)
        {
            throw new JsonException(ex.Message);
        }
    }

    public override void Write(Utf8JsonWriter writer, long value, JsonSerializerOptions options)
        => writer.WriteNumberValue(value);
}
