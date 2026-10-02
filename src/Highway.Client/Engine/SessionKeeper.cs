using System.Text.Json;
using Highway.Abstractions;
using Microsoft.Extensions.Logging;

namespace Highway.Client.Engine;

/// <summary>
/// Everything the broker holds on this node's behalf, declared as one <c>HW.SESSION</c> (protocol 5.0,
/// features 067/068): the catalog (only when heartbeats are on), the node's group subscriptions, and its
/// recurring-job schedules.
///
/// <para><b>Why one declaration, re-sent.</b> Before 3.0 subscriptions and schedules were sent once, at
/// start. A broker that lost them — wiped, a fresh node, a re-bootstrapped standby — kept answering, but a
/// publish reached nobody and jobs never fired, silently, until the client restarted; only registration
/// healed itself. Now the whole session is re-declared whenever the broker says it has none
/// (<c>+SESSION</c> on a heartbeat) and after every failover adoption. The command is idempotent, so a
/// redundant declaration costs one round trip and changes nothing.</para>
///
/// <para><b>No catalog without heartbeats.</b> A registered node that never beats looks dead; dead-node
/// recovery would take its work and retire its groups. With heartbeats disabled the session carries only
/// subscriptions and jobs, exactly what was sent independently before.</para>
/// </summary>
internal sealed class SessionKeeper
{
    private readonly IHighwayConnection _connection;
    private readonly string _nodeName;
    private readonly byte[] _sessionJson;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _flight = new(1, 1);

    internal sealed record Subscription(string Channel, string Group);

    internal sealed record Job(string Queue, string Name, string Expression, byte[] Template);

    internal sealed record DeadLetterDeclaration(string Kind, string Name);
    internal sealed record PolicyDeclaration(string Kind, string Name, int MaxAttempts, string Backoff, int MaxRequeues);

    /// <summary>
    /// This process, for display (072): the broker keeps it in memory against the instance id the
    /// connections carry, so its dashboard can say which processes stand behind a node name.
    /// </summary>
    internal sealed record Instance(string Id, string Host, int Pid, string? Hosting, DateTimeOffset Started, string Client)
    {
        public static Instance Of(HighwayOptions options)
        {
            DateTimeOffset started;
            try { started = System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime(); }
            catch (Exception) { started = DateTimeOffset.UtcNow; }   // not readable everywhere; display only
            var client = typeof(SessionKeeper).Assembly
                .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "";
            var plus = client.IndexOf('+');
            return new Instance(options.InstanceId, Environment.MachineName, Environment.ProcessId,
                options.HostingName, started, plus < 0 ? client : client[..plus]);
        }
    }

    public SessionKeeper(
        IHighwayConnection connection,
        string nodeName,
        CatalogInfo? catalog,
        IReadOnlyList<Subscription> subscriptions,
        IReadOnlyList<Job> jobs,
        ILogger logger,
        Instance? instance = null,
        IReadOnlyList<DeadLetterDeclaration>? deadLetters = null,
        IReadOnlyList<PolicyDeclaration>? policies = null)
    {
        _connection = connection;
        _nodeName = nodeName;
        _logger = logger;
        CatalogBytes = catalog is null ? 0 : JsonSerializer.SerializeToUtf8Bytes(catalog, Wire.HighwayJson.SerializerOptions).Length;

        // Serialized once: nothing in a session changes for the life of the node.
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            if (catalog is not null)
            {
                writer.WritePropertyName("catalog");
                JsonSerializer.Serialize(writer, catalog, Wire.HighwayJson.SerializerOptions);
            }
            writer.WriteStartArray("subscriptions");
            foreach (var s in subscriptions)
            {
                writer.WriteStartObject();
                writer.WriteString("channel", s.Channel);
                writer.WriteString("group", s.Group);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartArray("jobs");
            foreach (var j in jobs)
            {
                writer.WriteStartObject();
                writer.WriteString("queue", j.Queue);
                writer.WriteString("job", j.Name);
                writer.WriteString("expression", j.Expression);
                writer.WriteBase64String("template", j.Template);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            if (deadLetters is not null)
            {
                writer.WriteStartArray("deadLetters");
                foreach (var d in deadLetters)
                {
                    writer.WriteStartObject();
                    writer.WriteString("kind", d.Kind);
                    writer.WriteString("name", d.Name);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }
            if (policies is { Count: > 0 })
            {
                writer.WriteStartArray("policies");
                foreach (var p in policies)
                {
                    writer.WriteStartObject();
                    writer.WriteString("kind", p.Kind);
                    writer.WriteString("name", p.Name);
                    writer.WriteNumber("maxAttempts", p.MaxAttempts);
                    writer.WriteString("backoff", p.Backoff);
                    writer.WriteNumber("maxRequeues", p.MaxRequeues);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }
            if (instance is not null)
            {
                writer.WriteStartObject("instance");
                writer.WriteString("id", instance.Id);
                writer.WriteString("host", instance.Host);
                writer.WriteNumber("pid", instance.Pid);
                if (instance.Hosting is not null) writer.WriteString("hosting", instance.Hosting);
                writer.WriteString("started", instance.Started.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
                writer.WriteString("client", instance.Client);
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
        }
        _sessionJson = buffer.ToArray();
    }

    /// <summary>Size of the catalog part, for the start-up log line.</summary>
    public int CatalogBytes { get; }

    /// <summary>Declares the session; throws on failure. Used at start, where a failure must fail the start.</summary>
    public async Task EstablishAsync(CancellationToken ct)
    {
        await _flight.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _connection.EstablishSessionAsync(_nodeName, _sessionJson, ct).ConfigureAwait(false);
        }
        finally
        {
            _flight.Release();
        }
    }

    /// <summary>
    /// Re-declares the session without throwing: after <c>+SESSION</c> or a failover. Single-flight — a
    /// declaration already under way covers this one — and a failure is logged and left to the next trigger
    /// (the next beat answers <c>+SESSION</c> again).
    /// </summary>
    public async Task ReestablishAsync(string reason, CancellationToken ct = default)
    {
        if (!await _flight.WaitAsync(0, ct).ConfigureAwait(false))
            return;
        try
        {
            await _connection.EstablishSessionAsync(_nodeName, _sessionJson, ct).ConfigureAwait(false);
            _logger.LogInformation("Session re-established for '{Node}' ({Reason})", _nodeName, reason);
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Could not re-establish the session for '{Node}' ({Reason}); the next heartbeat retries", _nodeName, reason);
        }
        finally
        {
            _flight.Release();
        }
    }
}
