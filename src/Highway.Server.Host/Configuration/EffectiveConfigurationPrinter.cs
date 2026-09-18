namespace Highway.Server.Host.Configuration;

/// <summary>
/// Prints the effective configuration for <c>--validate</c> (feature 031 R1.4), with
/// secrets masked: a password on a console is a password in a screen-share, a log
/// aggregator and a support ticket.
/// </summary>
internal static class EffectiveConfigurationPrinter
{
    private const string Masked = "********";
    private const string NotSet = "(not set)";

    public static void Print(HostConfiguration c, TextWriter writer)
    {
        writer.WriteLine("Effective configuration (secrets masked):");
        writer.WriteLine();
        writer.WriteLine("  server");
        writer.WriteLine($"    port                             : {c.Server.Port}");
        writer.WriteLine($"    bindAddress                      : {c.Server.BindAddress}");
        writer.WriteLine($"    dataDir                          : {Value(c.Server.DataDir)}");
        writer.WriteLine($"    ephemeral                        : {c.Server.Ephemeral}");
        writer.WriteLine($"    aofSizeLimitBytes                : {c.Server.AofSizeLimitBytes}");
        writer.WriteLine($"    maxQueueBytes                    : {c.Server.MaxQueueBytes}");
        writer.WriteLine($"    lease                            : {c.Server.Lease}");
        writer.WriteLine($"    replySlotTtl                     : {c.Server.ReplySlotTtl}");
        writer.WriteLine($"    maxPayloadBytes                  : {c.Server.MaxPayloadBytes}");
        writer.WriteLine($"    maxIdentifierBytes               : {c.Server.MaxIdentifierBytes}");
        writer.WriteLine($"    nodeExpiry                       : {c.Server.NodeExpiry}");
        writer.WriteLine($"    pruningEnabled                   : {c.Server.PruningEnabled}");
        writer.WriteLine($"    maxCatalogBytes                  : {c.Server.MaxCatalogBytes}");
        writer.WriteLine($"    subscriberRetirementThreshold    : {c.Server.SubscriberRetirementThreshold}");
        writer.WriteLine($"    maxDeliveryAttempts              : {c.Server.MaxDeliveryAttempts}");
        writer.WriteLine($"    maxDeadLetterEntries             : {c.Server.MaxDeadLetterEntries}");
        writer.WriteLine($"    pubSubBackoffEnabled             : {c.Server.PubSubBackoffEnabled}");
        writer.WriteLine($"    rpcBackoffEnabled                : {c.Server.RpcBackoffEnabled}");
        writer.WriteLine($"    maxBackoff                       : {c.Server.MaxBackoff}");
        writer.WriteLine($"    receiveDefaultCount              : {c.Server.ReceiveDefaultCount}");
        writer.WriteLine($"    receiveMaxCount                  : {c.Server.ReceiveMaxCount}");
        writer.WriteLine($"    waitForCommit                    : {c.Server.WaitForCommit}");
        writer.WriteLine($"    replication.startAsReplica       : {c.Server.Replication.StartAsReplica}");
        writer.WriteLine($"    replication.replicaId            : {c.Server.Replication.ReplicaId}");
        writer.WriteLine($"    replication.priority             : {c.Server.Replication.Priority}");
        writer.WriteLine($"    replication.advertiseEndpoint    : {c.Server.Replication.AdvertiseEndpoint ?? "(own endpoint)"}");
        writer.WriteLine($"    replication.primaryServer        : {Redact(c.Server.Replication.PrimaryServer)}");
        writer.WriteLine($"    replication.slotLagCapSequences  : {c.Server.Replication.SlotLagCapSequences}");
        writer.WriteLine($"    replication.slotStaleAfter       : {c.Server.Replication.SlotStaleAfter}");
        writer.WriteLine($"    replication.autoFailover         : {c.Server.Replication.AutoFailover}");
        writer.WriteLine($"    replication.autoRejoin           : {c.Server.Replication.AutoRejoin}");
        writer.WriteLine($"    replication.fenceTimeout         : {c.Server.Replication.FenceTimeout}");
        writer.WriteLine($"    replication.promoteTimeout       : {c.Server.Replication.PromoteTimeout}");
        writer.WriteLine($"    replication.margin               : {c.Server.Replication.Margin}");
        writer.WriteLine($"    replication.willingnessThreshold : {c.Server.Replication.WillingnessThreshold}");
        writer.WriteLine($"    replication.goodbyeDrainTimeout  : {c.Server.Replication.GoodbyeDrainTimeout}");
        writer.WriteLine($"    replication.walTtlSeconds        : {c.Server.Replication.WalTtlSeconds}");
        writer.WriteLine($"    replication.maxTotalWalSizeBytes : {c.Server.Replication.MaxTotalWalSizeBytes}");
        writer.WriteLine($"    cache.enabled                    : {c.Server.Cache.Enabled}");
        writer.WriteLine($"    cache.defaultTtl                 : {c.Server.Cache.DefaultTtl}");
        writer.WriteLine($"    cache.maxTtl                     : {c.Server.Cache.MaxTtl}");
        writer.WriteLine($"    cache.maxSizeBytes               : {c.Server.Cache.MaxSizeBytes}");
        writer.WriteLine($"    cache.sweepInterval              : {c.Server.Cache.SweepInterval}");
        writer.WriteLine($"    observability.recorderEnabled    : {c.Server.Observability.RecorderEnabled}");
        writer.WriteLine($"    observability.defaultCapacity    : {c.Server.Observability.DefaultCapacity}");
        writer.WriteLine($"    observability.defaultRetention   : {c.Server.Observability.DefaultRetention}");
        writer.WriteLine($"    observability.defaultCapture     : {c.Server.Observability.DefaultCapture}");
        writer.WriteLine($"    observability.replayEnabled      : {c.Server.Observability.ReplayEnabled}");
        writer.WriteLine($"    observability.activitiesEnabled  : {c.Server.Observability.ActivitiesEnabled}");
        writer.WriteLine();
        writer.WriteLine("  authentication");
        writer.WriteLine($"    password                         : {Secret(c.Authentication.Password)}");
        writer.WriteLine();
        writer.WriteLine("  tls");
        writer.WriteLine($"    certFile                         : {Value(c.Tls.CertFile)}");
        writer.WriteLine($"    certPassword                     : {Secret(c.Tls.CertPassword)}");
        writer.WriteLine($"    certSubjectName                  : {Value(c.Tls.CertSubjectName)}");
        writer.WriteLine($"    clientCertificateRequired        : {c.Tls.ClientCertificateRequired}");
        writer.WriteLine($"    revocationMode                   : {c.Tls.RevocationMode}");
        writer.WriteLine($"    issuerCertificatePath            : {Value(c.Tls.IssuerCertificatePath)}");
        writer.WriteLine($"    refreshFrequencySeconds          : {c.Tls.RefreshFrequencySeconds}");
        writer.WriteLine();
        writer.WriteLine("  dashboard");
        writer.WriteLine($"    enabled                          : {c.Dashboard.Enabled}");
        writer.WriteLine($"    healthEndpoints                  : {c.Dashboard.HealthEndpoints}");
        writer.WriteLine($"    port                             : {c.Dashboard.Port}");
        writer.WriteLine($"    bindAddress                      : {c.Dashboard.BindAddress}");
        writer.WriteLine($"    pathBase                         : {(c.Dashboard.PathBase.Length == 0 ? "(root)" : c.Dashboard.PathBase)}");
        writer.WriteLine($"    apiKey                           : {Secret(c.Dashboard.ApiKey)}");
        writer.WriteLine($"    maxConcurrentStreams             : {c.Dashboard.MaxConcurrentStreams}");
        writer.WriteLine($"    streamBufferCapacity             : {c.Dashboard.StreamBufferCapacity}");
        writer.WriteLine($"    keepAliveInterval                : {c.Dashboard.KeepAliveInterval}");
    }

    private static string Value(string? value)
        => string.IsNullOrEmpty(value) ? NotSet : value;

    private static string Secret(string? value)
        => string.IsNullOrEmpty(value) ? NotSet : Masked;

    /// <summary>A connection string with any <c>password=…</c> component masked; the host stays legible.</summary>
    private static string Redact(string? connectionString)
        => string.IsNullOrEmpty(connectionString)
            ? NotSet
            : System.Text.RegularExpressions.Regex.Replace(
                connectionString, @"(password\s*=\s*)[^,]*", $"$1{Masked}",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
}
