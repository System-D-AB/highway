using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Pipelines;
using Highway.Server.Commands.Runtime;

namespace Highway.Server.Resp;

/// <summary>
/// The in-process doorbell pub/sub registry (040 T7, 037 R7). Two faces:
/// <list type="bullet">
///   <item>the <b>command side</b> rings a channel through <see cref="IDoorbell"/> — a
///     server-internal publish, never over the wire, never stored (<c>hw:door:*</c> touches no
///     RocksDB, physical-layout §3);</item>
///   <item>the <b>connection side</b> registers via <see cref="CreateSubscriber"/> and receives a
///     RESP push frame per matching ring.</item>
/// </list>
///
/// <para><b>Lossy by spec (037 R7).</b> A publish snapshots the channel's subscriber list and
/// writes each a push frame; a write that faults (a subscriber that vanished mid-publish) costs
/// nothing but that one delivery — correctness rides on the backstop sweep, not on delivery.
/// <c>PUBLISH</c> is never served to clients; only the broker rings.</para>
/// </summary>
internal sealed class SubscriptionRegistry : IDoorbell
{
    // channel → (connectionId → connection). ConcurrentDictionary for lock-free reads on the ring path.
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, Connection>> _byChannel = new();
    private readonly ConcurrentDictionary<string, Connection> _byConnection = new();

    /// <summary>One connected subscriber: its output pipe and the channels it holds.</summary>
    private sealed class Connection(string id, PipeWriter output)
    {
        public string Id { get; } = id;
        public PipeWriter Output { get; } = output;
        public HashSet<string> Channels { get; } = [];
        public readonly Lock WriteGate = new();
    }

    /// <summary>Registers a connection and returns its per-connection subscribe/unsubscribe sink.</summary>
    public ISubscriptionSink CreateSubscriber(string connectionId, PipeWriter output)
    {
        var connection = new Connection(connectionId, output);
        _byConnection[connectionId] = connection;
        return new Sink(this, connection);
    }

    /// <summary>Drops a connection and all its channel memberships (teardown).</summary>
    public void RemoveSubscriber(string connectionId)
    {
        if (!_byConnection.TryRemove(connectionId, out var connection))
            return;

        lock (connection.WriteGate)
        {
            foreach (var channel in connection.Channels)
                if (_byChannel.TryGetValue(channel, out var set))
                    set.TryRemove(connectionId, out _);
        }
    }

    /// <summary>
    /// Rings <paramref name="channel"/>: writes a <c>message</c> push frame to every subscriber.
    /// Returns the number reached. Best-effort — a faulted write drops only that delivery.
    /// </summary>
    public int Ring(string channel, ReadOnlySpan<byte> payload)
    {
        if (!_byChannel.TryGetValue(channel, out var subscribers) || subscribers.IsEmpty)
            return 0;

        var channelBytes = System.Text.Encoding.UTF8.GetBytes(channel);
        var payloadBytes = payload.ToArray();

        var writer = new RespWriter();
        writer.PushMessage(channelBytes, payloadBytes);
        var frame = writer.ToArray();

        var reached = 0;
        foreach (var connection in subscribers.Values)
        {
            if (TryPush(connection, frame)) reached++;
        }
        return reached;
    }

    private static bool TryPush(Connection connection, byte[] frame)
    {
        try
        {
            // A per-connection gate so a push frame and a command reply never interleave bytes.
            lock (connection.WriteGate)
            {
                connection.Output.Write(frame);
                var flush = connection.Output.FlushAsync();
                if (!flush.IsCompleted)
                    flush.AsTask().GetAwaiter().GetResult();
            }
            return true;
        }
        catch
        {
            return false; // lossy by spec — the backstop sweep is the safety net
        }
    }

    /// <summary>The per-connection sink handed to a <see cref="RespSession"/>.</summary>
    private sealed class Sink(SubscriptionRegistry registry, Connection connection) : ISubscriptionSink
    {
        public int Subscribe(string channel)
        {
            var set = registry._byChannel.GetOrAdd(channel, _ => new ConcurrentDictionary<string, Connection>());
            set[connection.Id] = connection;
            lock (connection.WriteGate) connection.Channels.Add(channel);
            return Count;
        }

        public int Unsubscribe(string channel)
        {
            if (registry._byChannel.TryGetValue(channel, out var set))
                set.TryRemove(connection.Id, out _);
            lock (connection.WriteGate) connection.Channels.Remove(channel);
            return Count;
        }

        public int Count
        {
            get { lock (connection.WriteGate) return connection.Channels.Count; }
        }
    }
}
