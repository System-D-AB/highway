using System.Text;
using Highway.Server.Commands.Runtime;
using Highway.Server.Internal;
using Highway.Server.Storage.Replication;

namespace Highway.Server.Commands.Ported;

/// <summary>
/// Two forms (042 T1 + 042-1a D2):
///
/// <para><b>Replica:</b> <c>HW.REPL.HELLO &lt;replicaId&gt; &lt;position&gt; &lt;epoch&gt;
/// [endpoint] FORMAT &lt;n&gt; [REPL &lt;replicationEndpoint&gt;] [NODE &lt;storeNodeId&gt;]</c> →
/// <c>[primaryEpoch, minAcked, format, replicationEndpoint]</c>: a standby's heartbeat (contact and epoch, 076) and a
/// promoting node's announce. The optional endpoint names where the caller is reachable — a promoting node announces
/// itself with it, so a demoted primary's <c>-NOTPRIMARY</c> can redirect clients to the real primary; <c>REPL</c>
/// names where its data is followed. The reply names this node's replication endpoint, which a standby follows.</para>
///
/// <para><b>Client (the herd handshake):</b> <c>HW.REPL.HELLO CLIENT &lt;clientId&gt;
/// &lt;lastSeenEpoch&gt;</c> → <c>["master"|"willing", epoch, rosterVersion]</c> when this
/// node is the master or is willing to accept the herd, else
/// <c>["standby", masterEndpoint, masterEpoch]</c> — the redirect a confused client
/// follows. Willingness is the contract predicate (<see cref="HighwayReplication.IsWillingForHerd"/>);
/// answering it never promotes — promotion happens on the first accepted client verb
/// (042-1c).</para>
///
/// <para><b>Peer (069 R1):</b> <c>HW.REPL.HELLO PEER &lt;nodeId&gt; &lt;lastSeenEpoch&gt;</c> answers
/// exactly as the client form, but its connection is a peer session: it is not counted as a herd
/// client, and it is not master contact. A standby that has lost its master asks its higher-priority
/// peers this before it answers a client willing.</para>
/// </summary>
internal sealed class HwReplHelloCommand : HighwayCommand
{
    private const string ClientForm = "CLIENT";
    private const string PeerForm = "PEER";

    private bool _isClientForm;
    private string _replicaId = "";
    private ulong _lastApplied;
    private ulong _epoch;
    private string? _endpoint;
    private int? _format;
    private string? _replicationEndpoint;
    private string? _storeNodeId;

    protected override bool Parse(CommandContext ctx, CommandInput input)
    {
        var idx = 0;
        var first = input.Next(ref idx);
        if (first.IsEmpty)
            return Fail(HighwayErrors.InvalidArg, "replicaId is blank");

        var firstText = Encoding.UTF8.GetString(first);
        var isPeer = firstText.Equals(PeerForm, StringComparison.OrdinalIgnoreCase);
        if (isPeer || firstText.Equals(ClientForm, StringComparison.OrdinalIgnoreCase))
        {
            _isClientForm = true;   // the peer form answers exactly as the client form
            if (!TryReadIdentifier(input, ref idx, isPeer ? "nodeId" : "clientId", ctx.Options.MaxIdentifierBytes, out _replicaId))
                return false;
            return TryReadUInt64(input, ref idx, "lastSeenEpoch", out _epoch);
        }

        if (!Identifier.IsValid(first, ctx.Options.MaxIdentifierBytes))
            return Fail(HighwayErrors.InvalidArg, IdentifierErrorDetail(first, "replicaId", ctx.Options.MaxIdentifierBytes));
        _replicaId = firstText;
        if (!TryReadUInt64(input, ref idx, "lastAppliedSeq", out _lastApplied))
            return false;
        if (!TryReadUInt64(input, ref idx, "epoch", out _epoch))
            return false;
        // Optional, in any order: <endpoint>, FORMAT <n> (065 T6), REPL <replicationEndpoint> and NODE <storeNodeId> (076).
        for (var arg = input.Next(ref idx); !arg.IsEmpty; arg = input.Next(ref idx))
        {
            var text = Encoding.UTF8.GetString(arg);
            if (text.Equals("FORMAT", StringComparison.OrdinalIgnoreCase))
            {
                if (!int.TryParse(Encoding.ASCII.GetString(input.Next(ref idx)), out var format))
                    return Fail(HighwayErrors.InvalidArg, "FORMAT requires the storage format number");
                _format = format;
            }
            else if (text.Equals("REPL", StringComparison.OrdinalIgnoreCase))
            {
                var value = input.Next(ref idx);
                if (value.IsEmpty) return Fail(HighwayErrors.InvalidArg, "REPL requires the replication endpoint");
                _replicationEndpoint = Encoding.UTF8.GetString(value);
            }
            else if (text.Equals("NODE", StringComparison.OrdinalIgnoreCase))
            {
                var value = input.Next(ref idx);
                if (value.IsEmpty) return Fail(HighwayErrors.InvalidArg, "NODE requires the store node id");
                _storeNodeId = Encoding.UTF8.GetString(value);
            }
            else
            {
                _endpoint = text;
            }
        }
        return true;
    }

    protected override void Run(CommandContext ctx, RespWriter writer)
    {
        if (!TryFeeder(ctx, writer, out var feeder))
            return;

        if (_isClientForm)
        {
            RunClientHandshake(ctx, writer, feeder);
            return;
        }

        // 065 T6: a replica holds this primary's data key for key, so it must store the same layout.
        // A 2.x node (format 3) sends no FORMAT: it must never follow 3.0 data into its old keyspace.
        if (_format != HighwayServerBuilder.StorageFormatVersion)
        {
            writer.Error(HighwayErrors.Format(HighwayErrors.StorageFormat,
                $"replica '{_replicaId}' stores format {(_format?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "3 (Highway 2.x)")}; " +
                $"this primary stores format {HighwayServerBuilder.StorageFormatVersion}. A replica must run the same " +
                "major version: upgrade it, run 'highways --wipe-data', and let it re-sync from this primary."));
            return;
        }

        var (epoch, minSeq) = feeder.Hello(_replicaId, _lastApplied, _epoch, _endpoint, _replicationEndpoint, _storeNodeId);
        // The reply echoes this primary's format (a 2.x primary ignores FORMAT and answers two integers, which a 3.0
        // replica must not take for consent) and names where this primary's data is followed (076).
        writer.HelloReply((long)epoch, (long)minSeq, HighwayServerBuilder.StorageFormatVersion, feeder.ReplicationEndpoint,
            feeder.IsDataLive(_storeNodeId));
    }

    private void RunClientHandshake(CommandContext ctx, RespWriter writer, HighwayReplication feeder)
    {
        // A client that saw a higher epoch informs (RD7's any-channel rule).
        if (_epoch > feeder.Epoch)
            feeder.ObserveHigherEpoch(_epoch, "client handshake carried a higher epoch");

        // A draining master answers "standby": it is leaving, and advertising "master"
        // would pull the walking herd straight back onto the departing node (R12).
        if ((feeder.IsWritable && !feeder.IsDraining) || feeder.IsWillingForHerd())
        {
            var rosterVersion = ReadRosterVersion(ctx);
            writer.BulkStringArray(
                Encoding.UTF8.GetBytes(feeder.IsWritable ? "master" : "willing"),
                Encoding.UTF8.GetBytes(feeder.Epoch.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                Encoding.UTF8.GetBytes(rosterVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            return;
        }

        writer.BulkStringArray(
            "standby"u8.ToArray(),
            Encoding.UTF8.GetBytes(feeder.RedirectEndpoint()),
            Encoding.UTF8.GetBytes(feeder.Epoch.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }

    internal static ulong ReadRosterVersion(CommandContext ctx)
    {
        using var snap = ctx.Store.Snapshot();
        var raw = ctx.Store.Get(snap, Highway.Server.Storage.Layout.HighwayKeyspace.Kv(RosterRecord.StoreName));
        return raw is null ? 0 : RosterRecord.Decode(raw).Version;
    }

    internal static bool TryFeeder(CommandContext ctx, RespWriter writer, out HighwayReplication feeder)
    {
        if (ctx.Replication is { } f)
        {
            feeder = f;
            return true;
        }

        feeder = null!;
        writer.Error(HighwayErrors.Format(HighwayErrors.InvalidArg,
            "replication requires a durable broker; this instance is ephemeral"));
        return false;
    }
}

/// <summary>HW.REPL.PROMOTE [reason] → :epoch (042 T5).</summary>
internal sealed class HwReplPromoteCommand : HighwayCommand
{
    private string _reason = "admin";

    protected override bool Parse(CommandContext ctx, CommandInput input)
    {
        var idx = 0;
        var raw = input.Next(ref idx);
        if (!raw.IsEmpty)
            _reason = Encoding.UTF8.GetString(raw);
        return true;
    }

    protected override void Run(CommandContext ctx, RespWriter writer)
    {
        if (!HwReplHelloCommand.TryFeeder(ctx, writer, out var feeder))
            return;
        if (!feeder.TryPromote(_reason, out var error))
        {
            writer.Error(HighwayErrors.Format(HighwayErrors.InvalidArg, error!));
            return;
        }
        writer.Integer((long)feeder.Epoch);
    }
}

/// <summary>
/// HW.REPL.GOODBYE [reason] → +OK (042-1c C-T5 / parent R12). Begins the graceful drain:
/// GOODBYE is narrated to the herd, new master-only work is refused, in-flight completes
/// (bounded by <c>GoodbyeDrainTimeout</c>), then the node stands down cleanly. Idempotent;
/// a no-op on a node that is not the primary (nothing to drain).
/// </summary>
internal sealed class HwReplGoodbyeCommand : HighwayCommand
{
    private string _reason = "operator";

    protected override bool Parse(CommandContext ctx, CommandInput input)
    {
        var idx = 0;
        var raw = input.Next(ref idx);
        if (!raw.IsEmpty)
            _reason = Encoding.UTF8.GetString(raw);
        return true;
    }

    protected override void Run(CommandContext ctx, RespWriter writer)
    {
        if (!HwReplHelloCommand.TryFeeder(ctx, writer, out var feeder))
            return;
        feeder.BeginGoodbye(_reason);
        writer.SimpleString("OK");
    }
}

/// <summary>HW.REPL.FENCE [reason] → +OK (042 T5/T7). Primary-only; a no-op on a replica.</summary>
internal sealed class HwReplFenceCommand : HighwayCommand
{
    private string _reason = "admin";

    protected override bool Parse(CommandContext ctx, CommandInput input)
    {
        var idx = 0;
        var raw = input.Next(ref idx);
        if (!raw.IsEmpty)
            _reason = Encoding.UTF8.GetString(raw);
        return true;
    }

    protected override void Run(CommandContext ctx, RespWriter writer)
    {
        if (!HwReplHelloCommand.TryFeeder(ctx, writer, out var feeder))
            return;
        feeder.Fence(_reason);
        writer.SimpleString("OK");
    }
}

/// <summary>
/// HW.REPL.STATUS → flat field/value array (042 T4/T5; 042-1a adds the <c>roster.*</c>
/// fields — the client's read of the live roster, parent R13.2).
/// </summary>
internal sealed class HwReplStatusCommand : HighwayCommand
{
    protected override bool Parse(CommandContext ctx, CommandInput input) => true;

    protected override void Run(CommandContext ctx, RespWriter writer)
    {
        if (!HwReplHelloCommand.TryFeeder(ctx, writer, out var feeder))
            return;

        var fields = feeder.StatsFields().ToList();

        using var snap = ctx.Store.Snapshot();
        var raw = ctx.Store.Get(snap, Highway.Server.Storage.Layout.HighwayKeyspace.Kv(RosterRecord.StoreName));
        var roster = raw is null ? RosterRecord.Empty : RosterRecord.Decode(raw);
        fields.Add(("roster.version", roster.Version.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        var i = 0;
        foreach (var m in roster.Members)
        {
            fields.Add(($"roster.{i}.id", m.NodeId));
            fields.Add(($"roster.{i}.priority", m.Priority.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            fields.Add(($"roster.{i}.endpoint", m.Endpoint));
            i++;
        }

        writer.FieldArray(fields);
    }
}

/// <summary>
/// HW.REPL.WITNESS → +OK — a bare liveness probe. The 042 peer-question form
/// (<c>WITNESS &lt;nodeId&gt; &lt;role&gt;</c>) was withdrawn by 042-1a: herd-driven
/// promotion removed the timer path the witness gated (see the reconciliation map).
/// </summary>
internal sealed class HwReplWitnessCommand : HighwayCommand
{
    protected override bool Parse(CommandContext ctx, CommandInput input) => true;

    protected override void Run(CommandContext ctx, RespWriter writer)
    {
        if (!HwReplHelloCommand.TryFeeder(ctx, writer, out _))
            return;
        writer.SimpleString("OK");
    }
}

/// <summary>
/// HW.REPL.JOIN &lt;nodeId&gt; &lt;priority&gt; &lt;endpoint&gt; → :rosterVersion (042-1a D5 /
/// parent R13). Master-only (the dispatcher's writability gate refuses it elsewhere with
/// <c>-NOTPRIMARY</c>, which is itself the joiner's redirect to the master). Admits the
/// node into the roster — stored as replicated KV <c>repl:roster</c>, so it WAL-ships to
/// every standby — or refuses a held priority with <c>HW_PRIORITY_TAKEN</c> naming the
/// holder (first announcer wins; succession must never depend on restart order).
/// </summary>
internal sealed class HwReplJoinCommand : HighwayCommand
{
    private string _nodeId = "";
    private int _priority;
    private string _endpoint = "";

    protected override bool Parse(CommandContext ctx, CommandInput input)
    {
        var idx = 0;
        if (!TryReadIdentifier(input, ref idx, "nodeId", ctx.Options.MaxIdentifierBytes, out _nodeId))
            return false;
        if (!TryReadUInt64(input, ref idx, "priority", out var priority) || priority > int.MaxValue)
            return Fail(HighwayErrors.InvalidArg, "priority must be an integer in 0..2147483647");
        _priority = (int)priority;
        var endpoint = input.Next(ref idx);
        if (endpoint.IsEmpty)
            return Fail(HighwayErrors.InvalidArg, "endpoint is blank");
        _endpoint = Encoding.UTF8.GetString(endpoint);
        return true;
    }

    private ulong _admittedVersion;

    protected override void Run(CommandContext ctx, RespWriter writer)
    {
        if (!HwReplHelloCommand.TryFeeder(ctx, writer, out _))
            return;

        using var _lock = ctx.Locks.Lock(RosterRecord.StoreName);

        if (!RosterStore.TryUpsert(ctx.Store, new RosterMember(_nodeId, _priority, _endpoint),
                out var updated, out var holder))
        {
            writer.Error(HighwayErrors.Format(HighwayErrors.PriorityTaken,
                $"priority={_priority} holder={holder}; fix this node's config"));
            return;
        }

        _admittedVersion = updated.Version;
        writer.Integer((long)updated.Version);
    }

    protected override void AfterCommit(CommandContext ctx)
    {
        if (Failed || _admittedVersion == 0) return;
        // Narrate the roster change to the herd (042-1c C-T3; advisory by contract).
        ctx.Doorbell.Ring("hw:door:topology",
            System.Text.Encoding.UTF8.GetBytes(
                $"ROSTER-UPDATE {_admittedVersion} {_nodeId} {_priority} {_endpoint} JOINED"));
    }
}
