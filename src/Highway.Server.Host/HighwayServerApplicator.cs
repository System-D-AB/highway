using System.Net;
using Garnet.server.Auth.Settings;
using Highway.Server.Dashboard;   // WithDashboard extension
using Highway.Server.Host.Configuration;

namespace Highway.Server.Host;

/// <summary>
/// Translates a loaded <see cref="HostConfiguration"/> into
/// <see cref="HighwayServerBuilder"/> calls (feature 031, design § Mapping onto the
/// builder). This is the only place the configuration model meets the server API —
/// the builder's existing validation (012's bind-address rule, certificate loading,
/// the storage-format guard) runs unchanged, and the host never reaches past the
/// builder into Garnet options.
/// </summary>
internal static class HighwayServerApplicator
{
    public static IHighwayServer BuildServer(HostConfiguration c, Microsoft.Extensions.Logging.ILoggerFactory loggerFactory)
    {
        var builder = new HighwayServerBuilder()
            .WithPort(c.Server.Port)
            .WithBindAddress(c.Server.BindAddress)
            .WithOptions(o => ApplyServerSection(c.Server, o))
            .WithLoggerFactory(loggerFactory);

        // Durability: ephemeral by name, or a path, or the builder's own durable default.
        if (c.Server.Ephemeral)
            builder.Ephemeral();
        else if (c.Server.DataDir is not null)
            builder.WithDataDir(c.Server.DataDir);

        // Authentication (feature 012): one mechanism — an ACL file replaces the password.
        if (c.Authentication.AclFile is not null)
            builder.WithAuthentication(new AclAuthenticationPasswordSettings(aclConfigurationFile: c.Authentication.AclFile));
        else if (c.Authentication.Password is not null)
            builder.WithPassword(c.Authentication.Password);

        // TLS (feature 012): PFX file or certificate-store subject name, plus the mTLS set.
        if (c.Tls.CertFile is not null || c.Tls.CertSubjectName is not null)
        {
            builder.WithTls(t =>
            {
                t.CertFileName = c.Tls.CertFile;
                t.CertPassword = c.Tls.CertPassword;
                t.CertSubjectName = c.Tls.CertSubjectName;
                t.ClientCertificateRequired = c.Tls.ClientCertificateRequired;
                t.CertificateRevocationCheckMode = c.Tls.RevocationMode;
                t.IssuerCertificatePath = c.Tls.IssuerCertificatePath;
                t.CertificateRefreshFrequencySeconds = c.Tls.RefreshFrequencySeconds;
            });
        }

        // Dashboard: opt-in, exactly as WithDashboard is today.
        if (c.Dashboard.Enabled)
        {
            builder.WithDashboard(d =>
            {
                d.Port = c.Dashboard.Port;
                d.Bind = IPAddress.Parse(c.Dashboard.BindAddress);
                d.PathBase = c.Dashboard.PathBase;
                d.ApiKey = c.Dashboard.ApiKey;
                d.MaxConcurrentStreams = c.Dashboard.MaxConcurrentStreams;
                d.StreamBufferCapacity = c.Dashboard.StreamBufferCapacity;
                d.KeepAliveInterval = c.Dashboard.KeepAliveInterval;
            });
        }

        return builder.Build();
    }

    private static void ApplyServerSection(ServerSection s, HighwayServerOptions o)
    {
        o.AofSizeLimitBytes = s.AofSizeLimitBytes;
        o.AofSegmentSize = s.AofSegmentSize;
        o.MaxQueueBytes = s.MaxQueueBytes;
        o.Lease = s.Lease;
        o.ReplySlotTtl = s.ReplySlotTtl;
        o.MaxPayloadBytes = s.MaxPayloadBytes;
        o.MaxIdentifierBytes = s.MaxIdentifierBytes;
        o.NodeExpiry = s.NodeExpiry;
        o.PruningEnabled = s.PruningEnabled;
        o.MaxCatalogBytes = s.MaxCatalogBytes;
        o.SubscriberRetirementThreshold = s.SubscriberRetirementThreshold;
        o.MaxDeliveryAttempts = s.MaxDeliveryAttempts;
        o.MaxDeadLetterEntries = s.MaxDeadLetterEntries;
        o.PubSubBackoffEnabled = s.PubSubBackoffEnabled;
        o.RpcBackoffEnabled = s.RpcBackoffEnabled;
        o.MaxBackoff = s.MaxBackoff;
        o.ReceiveDefaultCount = s.ReceiveDefaultCount;
        o.ReceiveMaxCount = s.ReceiveMaxCount;
        o.WaitForCommit = s.WaitForCommit;

        o.Observability.RecorderEnabled = s.Observability.RecorderEnabled;
        o.Observability.DefaultCapacity = s.Observability.DefaultCapacity;
        o.Observability.DefaultRetention = s.Observability.DefaultRetention;
        o.Observability.DefaultCapture = s.Observability.DefaultCapture;
        o.Observability.MaxBytes = s.Observability.MaxBytes;
        o.Observability.SweepInterval = s.Observability.SweepInterval;
        o.Observability.ReplayEnabled = s.Observability.ReplayEnabled;
        o.Observability.ReplayDefaultLimit = s.Observability.ReplayDefaultLimit;
        o.Observability.ReplayMaxLimit = s.Observability.ReplayMaxLimit;
        o.Observability.ReplayDefaultWindow = s.Observability.ReplayDefaultWindow;
        o.Observability.ActivitiesEnabled = s.Observability.ActivitiesEnabled;

        foreach (var (name, over) in s.Observability.Overrides)
        {
            o.Observability.Overrides[name] = new Observability.NameRecorderOptions
            {
                Capacity = over.Capacity,
                Retention = over.Retention,
                Capture = over.Capture,
            };
        }
    }
}
