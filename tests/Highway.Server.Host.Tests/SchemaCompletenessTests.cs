using FluentAssertions;
using Highway.Server;
using Highway.Server.Dashboard;
using Highway.Server.Host.Configuration;
using Highway.Server.Observability;
using Highway.Server.Security;
using Xunit;

namespace Highway.Server.Host.Tests;

/// <summary>
/// Feature 031 R2.1 — the schema binds 1:1 onto the options surface. These tests fail
/// when someone adds a public option to any of the server's classes without extending
/// the configuration schema — the defect class that turns a config file into a partial
/// truth. Each mapping names where the option lives in <c>highway.json</c>; escape
/// hatches that take live objects (not file-expressible) are recorded as exceptions.
/// </summary>
public class SchemaCompletenessTests
{
    private static readonly Dictionary<string, string> ServerLeaves = new()
    {
        ["Port"] = "server.port",
        ["BindAddress"] = "server.bindAddress",
        ["DataDir"] = "server.dataDir",
        ["Ephemeral"] = "server.ephemeral",
        ["AofSizeLimitBytes"] = "server.aofSizeLimitBytes",
        ["AofSegmentSize"] = "server.aofSegmentSize",
        ["MaxQueueBytes"] = "server.maxQueueBytes",
        ["Lease"] = "server.lease",
        ["ReplySlotTtl"] = "server.replySlotTtl",
        ["MaxPayloadBytes"] = "server.maxPayloadBytes",
        ["MaxIdentifierBytes"] = "server.maxIdentifierBytes",
        ["NodeExpiry"] = "server.nodeExpiry",
        ["PruningEnabled"] = "server.pruningEnabled",
        ["MaxCatalogBytes"] = "server.maxCatalogBytes",
        ["SubscriberRetirementThreshold"] = "server.subscriberRetirementThreshold",
        ["MaxDeliveryAttempts"] = "server.maxDeliveryAttempts",
        ["MaxDeadLetterEntries"] = "server.maxDeadLetterEntries",
        ["PubSubBackoffEnabled"] = "server.pubSubBackoffEnabled",
        ["RpcBackoffEnabled"] = "server.rpcBackoffEnabled",
        ["MaxBackoff"] = "server.maxBackoff",
        ["ReceiveDefaultCount"] = "server.receiveDefaultCount",
        ["ReceiveMaxCount"] = "server.receiveMaxCount",
        ["WaitForCommit"] = "server.waitForCommit",
    };

    private static readonly Dictionary<string, string> ServerSections = new()
    {
        ["Observability"] = "server.observability",
        ["Authentication"] = "authentication",
        ["Tls"] = "tls",
        ["Replication"] = "server.replication",
    };

    [Fact]
    public void EveryHighwayServerOption_IsReachableFromTheSchema()
    {
        foreach (var property in WritableProperties(typeof(HighwayServerOptions)))
        {
            if (ServerSections.TryGetValue(property.Name, out var section))
            {
                EnvironmentOverrides.LeafPaths
                    .Should().Contain(p => p.StartsWith(section + ".", StringComparison.OrdinalIgnoreCase),
                        $"the '{property.Name}' section must have schema entries");
                continue;
            }

            ServerLeaves.Should().ContainKey(property.Name,
                $"a new HighwayServerOptions option needs a highway.json entry (031 R2.1): '{property.Name}' has none");

            EnvironmentOverrides.LeafPaths.Should().Contain(ServerLeaves[property.Name],
                $"'{property.Name}' maps to '{ServerLeaves[property.Name]}' but the loader does not know that path");
        }
    }

    private static readonly Dictionary<string, string> DashboardLeaves = new()
    {
        ["Enabled"] = "dashboard.enabled",
        ["Port"] = "dashboard.port",
        ["Bind"] = "dashboard.bindAddress",
        ["PathBase"] = "dashboard.pathBase",
        ["ApiKey"] = "dashboard.apiKey",
        ["MaxConcurrentStreams"] = "dashboard.maxConcurrentStreams",
        ["StreamBufferCapacity"] = "dashboard.streamBufferCapacity",
        ["KeepAliveInterval"] = "dashboard.keepAliveInterval",
    };

    [Fact]
    public void EveryDashboardOption_IsReachableFromTheSchema()
    {
        foreach (var property in WritableProperties(typeof(DashboardOptions)))
        {
            DashboardLeaves.Should().ContainKey(property.Name,
                $"a new DashboardOptions option needs a highway.json entry (031 R2.1): '{property.Name}' has none");

            EnvironmentOverrides.LeafPaths.Should().Contain(DashboardLeaves[property.Name]);
        }
    }

    private static readonly Dictionary<string, string> ObservabilityLeaves = new()
    {
        ["RecorderEnabled"] = "server.observability.recorderEnabled",
        ["DefaultCapacity"] = "server.observability.defaultCapacity",
        ["DefaultRetention"] = "server.observability.defaultRetention",
        ["DefaultCapture"] = "server.observability.defaultCapture",
        ["MaxBytes"] = "server.observability.maxBytes",
        ["SweepInterval"] = "server.observability.sweepInterval",
        ["ReplayEnabled"] = "server.observability.replayEnabled",
        ["ReplayDefaultLimit"] = "server.observability.replayDefaultLimit",
        ["ReplayMaxLimit"] = "server.observability.replayMaxLimit",
        ["ReplayDefaultWindow"] = "server.observability.replayDefaultWindow",
        ["ActivitiesEnabled"] = "server.observability.activitiesEnabled",
    };

    // Per-name overrides are a map; one environment variable cannot address a map, so the
    // file is the only surface. Recorded, not silently skipped.
    private static readonly string[] ObservabilityFileOnly = ["Overrides"];

    [Fact]
    public void EveryObservabilityOption_IsReachableFromTheSchema()
    {
        foreach (var property in WritableProperties(typeof(ObservabilityOptions)))
        {
            if (ObservabilityFileOnly.Contains(property.Name))
                continue;

            ObservabilityLeaves.Should().ContainKey(property.Name,
                $"a new ObservabilityOptions option needs a highway.json entry (031 R2.1): '{property.Name}' has none");

            EnvironmentOverrides.LeafPaths.Should().Contain(ObservabilityLeaves[property.Name]);
        }
    }

    private static readonly Dictionary<string, string> ReplicationLeaves = new()
    {
        ["StartAsReplica"] = "server.replication.startAsReplica",
        ["ReplicaId"] = "server.replication.replicaId",
        ["Priority"] = "server.replication.priority",
        ["AdvertiseEndpoint"] = "server.replication.advertiseEndpoint",
        ["PrimaryServer"] = "server.replication.primaryServer",
        ["SlotLagCapSequences"] = "server.replication.slotLagCapSequences",
        ["AutoFailover"] = "server.replication.autoFailover",
        ["FenceTimeout"] = "server.replication.fenceTimeout",
        ["PromoteTimeout"] = "server.replication.promoteTimeout",
        ["Margin"] = "server.replication.margin",
        ["WillingnessThreshold"] = "server.replication.willingnessThreshold",
        ["GoodbyeDrainTimeout"] = "server.replication.goodbyeDrainTimeout",
        ["WalTtlSeconds"] = "server.replication.walTtlSeconds",
        ["MaxTotalWalSizeBytes"] = "server.replication.maxTotalWalSizeBytes",
    };

    // The deadman clock is a live object injected by tests (TimeProvider), not a file value.
    private static readonly string[] ReplicationFileExceptions = ["Clock"];

    [Fact]
    public void EveryReplicationOption_IsReachableFromTheSchema_OrRecordedAsAnException()
    {
        foreach (var property in WritableProperties(typeof(HighwayReplicationOptions)))
        {
            if (ReplicationFileExceptions.Contains(property.Name))
                continue;

            ReplicationLeaves.Should().ContainKey(property.Name,
                $"a new HighwayReplicationOptions option needs a highway.json entry (031 R2.1): '{property.Name}' has none");

            EnvironmentOverrides.LeafPaths.Should().Contain(ReplicationLeaves[property.Name],
                $"'{property.Name}' maps to '{ReplicationLeaves[property.Name]}' but the loader does not know that path");
        }
    }

    private static readonly Dictionary<string, string> AuthenticationLeaves = new()
    {
        ["Password"] = "authentication.password",
    };

    // Named users (the Users list) are minted with hashed passwords in code, not expressed as a
    // scalar highway.json leaf, so they are not part of the schema-completeness surface.
    private static readonly string[] AuthenticationFileExceptions = [];

    [Fact]
    public void EveryAuthenticationOption_IsReachableFromTheSchema_OrRecordedAsAnException()
    {
        foreach (var property in WritableProperties(typeof(AuthenticationOptions)))
        {
            if (AuthenticationFileExceptions.Contains(property.Name))
                continue;

            AuthenticationLeaves.Should().ContainKey(property.Name,
                $"a new AuthenticationOptions option needs a highway.json entry (031 R2.1): '{property.Name}' has none");

            EnvironmentOverrides.LeafPaths.Should().Contain(AuthenticationLeaves[property.Name]);
        }
    }

    private static readonly Dictionary<string, string> TlsLeaves = new()
    {
        ["CertFileName"] = "tls.certFile",
        ["CertPassword"] = "tls.certPassword",
        ["CertSubjectName"] = "tls.certSubjectName",
        ["ClientCertificateRequired"] = "tls.clientCertificateRequired",
        ["CertificateRevocationCheckMode"] = "tls.revocationMode",
        ["IssuerCertificatePath"] = "tls.issuerCertificatePath",
        ["CertificateRefreshFrequencySeconds"] = "tls.refreshFrequencySeconds",
    };

    // IsEphemeral is an in-memory test flag, not a file-expressible option.
    private static readonly string[] TlsFileExceptions = ["IsEphemeral"];

    [Fact]
    public void EveryTlsOption_IsReachableFromTheSchema_OrRecordedAsAnException()
    {
        foreach (var property in WritableProperties(typeof(TlsOptions)))
        {
            if (TlsFileExceptions.Contains(property.Name))
                continue;

            TlsLeaves.Should().ContainKey(property.Name,
                $"a new TlsOptions option needs a highway.json entry (031 R2.1): '{property.Name}' has none");

            EnvironmentOverrides.LeafPaths.Should().Contain(TlsLeaves[property.Name]);
        }
    }

    private static System.Reflection.PropertyInfo[] WritableProperties(Type type)
        => type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
               .Where(p => p.CanWrite)
               .ToArray();
}
