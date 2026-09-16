using System.Text;
using Highway.Server.Commands.Runtime;
using Highway.Server.Internal;
using Highway.Server.Storage.Rocks;

namespace Highway.Server.Commands.Ported;

/// <summary>
/// Two forms (042 T1 + 042-1a D2):
///
/// <para><b>Replica:</b> <c>HW.REPL.HELLO &lt;replicaId&gt; &lt;lastAppliedSeq&gt; &lt;epoch&gt;
/// [endpoint]</c> → <c>[primaryEpoch, minSeq]</c>. The optional endpoint names where the
/// caller is reachable — a promoting node announces itself with it, so a demoted
/// primary's <c>-NOTPRIMARY</c> can redirect clients to the real primary.</para>
///
/// <para><b>Client (the herd handshake):</b> <c>HW.REPL.HELLO CLIENT &lt;clientId&gt;
/// &lt;lastSeenEpoch&gt;</c> → <c>["master"|"willing", epoch, rosterVersion]</c> when this
/// node is the master or is willing to accept the herd, else
/// <c>["standby", masterEndpoint, masterEpoch]</c> — the redirect a confused client
/// follows. Willingness is the contract predicate (<see cref="ReplicationFeeder.IsWillingForHerd"/>);
/// answering it never promotes — promotion happens on the first accepted client verb
/// (042-1c).</para>
/// </summary>
internal sealed class HwReplHelloCommand : HighwayCommand
{
    private const string ClientForm = "CLIENT";

    private bool _isClientForm;
    private string _replicaId = "";
    private ulong _lastApplied;
    private ulong _epoch;
    private string? _endpoint;

    protected override bool Parse(CommandContext ctx, CommandInput input)
    {
        var idx = 0;
        var first = input.Next(ref idx);
        if (first.IsEmpty)
            return Fail(HighwayErrors.InvalidArg, "replicaId is blank");

        var firstText = Encoding.UTF8.GetString(first);
        if (firstText.Equals(ClientForm, StringComparison.OrdinalIgnoreCase))
        {
            _isClientForm = true;
            if (!TryReadIdentifier(input, ref idx, "clientId", ctx.Options.MaxIdentifierBytes, out _replicaId))
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
        var endpoint = input.Next(ref idx);
        if (!endpoint.IsEmpty)
            _endpoint = Encoding.UTF8.GetString(endpoint);
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

        var (epoch, minSeq) = feeder.Hello(_replicaId, _lastApplied, _epoch, _endpoint);
        writer.TwoIntegers((long)epoch, (long)minSeq);
    }

    private void RunClientHandshake(CommandContext ctx, RespWriter writer, ReplicationFeeder feeder)
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

    internal static bool TryFeeder(CommandContext ctx, RespWriter writer, out ReplicationFeeder feeder)
    {
        if (ctx.Replication is { } f)
        {
            feeder = f;
            return true;
        }

        feeder = null!;
        writer.Error(HighwayErrors.Format(HighwayErrors.InvalidArg,
            "replication requires a durable RocksDB broker; this instance is ephemeral"));
        return false;
    }
}

/// <summary>HW.REPL.PULL &lt;fromSeq&gt; &lt;maxBytes&gt; → [epoch, [[seq, data], ...], nextSeq|nil] (042 T1).</summary>
internal sealed class HwReplPullCommand : HighwayCommand
{
    private ulong _fromSeq;
    private int _maxBytes;

    protected override bool Parse(CommandContext ctx, CommandInput input)
    {
        var idx = 0;
        if (!TryReadUInt64(input, ref idx, "fromSeq", out _fromSeq))
            return false;
        if (!TryReadUInt64(input, ref idx, "maxBytes", out var maxBytes))
            return false;
        if (maxBytes == 0 || maxBytes > int.MaxValue)
            return Fail(HighwayErrors.InvalidArg, "maxBytes must be in 1..2147483647");
        _maxBytes = (int)maxBytes;
        return true;
    }

    protected override void Run(CommandContext ctx, RespWriter writer)
    {
        if (!HwReplHelloCommand.TryFeeder(ctx, writer, out var feeder))
            return;
        try
        {
            var page = feeder.Pull(_fromSeq, _maxBytes);
            writer.ReplicationPull(page.Epoch, page.Batches, page.NextSeq);
        }
        catch (ReplicationGapException gap)
        {
            // G4: never serve a gapped stream as contiguous — the replica re-bootstraps.
            writer.Error(HighwayErrors.Format(HighwayErrors.ReplGap,
                $"requested={gap.RequestedSeq} firstAvailable={gap.FirstAvailableSeq}; re-sync via HW.REPL.SNAPSHOT"));
        }
    }
}

/// <summary>HW.REPL.ACK &lt;replicaId&gt; &lt;appliedSeq&gt; → +OK (042 T1).</summary>
internal sealed class HwReplAckCommand : HighwayCommand
{
    private string _replicaId = "";
    private ulong _appliedSeq;

    protected override bool Parse(CommandContext ctx, CommandInput input)
    {
        var idx = 0;
        if (!TryReadIdentifier(input, ref idx, "replicaId", ctx.Options.MaxIdentifierBytes, out _replicaId))
            return false;
        return TryReadUInt64(input, ref idx, "appliedSeq", out _appliedSeq);
    }

    protected override void Run(CommandContext ctx, RespWriter writer)
    {
        if (!HwReplHelloCommand.TryFeeder(ctx, writer, out var feeder))
            return;
        if (!feeder.Ack(_replicaId, _appliedSeq))
        {
            writer.Error(HighwayErrors.Format(HighwayErrors.InvalidArg,
                $"replica '{_replicaId}' has no slot; send HW.REPL.HELLO first"));
            return;
        }
        writer.SimpleString("OK");
    }
}

/// <summary>
/// HW.REPL.SNAPSHOT BEGIN | GET &lt;session&gt; &lt;file&gt; &lt;offset&gt; &lt;maxBytes&gt; | END &lt;session&gt;
/// (042 T3). Streams a RocksDB checkpoint over RESP; the replica needs no shared filesystem.
/// </summary>
internal sealed class HwReplSnapshotCommand : HighwayCommand
{
    private enum Form { Begin, Get, End }
    private Form _form;
    private string _sessionId = "";
    private string _fileName = "";
    private ulong _offset;
    private int _maxBytes = 64 * 1024;

    protected override bool Parse(CommandContext ctx, CommandInput input)
    {
        var idx = 0;
        var verb = input.Next(ref idx);
        if (verb.IsEmpty)
            return Fail(HighwayErrors.InvalidArg, "SNAPSHOT requires BEGIN, GET, or END");

        var name = Encoding.UTF8.GetString(verb);
        if (name.Equals("BEGIN", StringComparison.OrdinalIgnoreCase))
        {
            _form = Form.Begin;
            return true;
        }

        if (name.Equals("END", StringComparison.OrdinalIgnoreCase))
        {
            _form = Form.End;
            var id = input.Next(ref idx);
            if (id.IsEmpty) return Fail(HighwayErrors.InvalidArg, "END requires a session id");
            _sessionId = Encoding.UTF8.GetString(id);
            return true;
        }

        if (!name.Equals("GET", StringComparison.OrdinalIgnoreCase))
            return Fail(HighwayErrors.InvalidArg, "SNAPSHOT form must be BEGIN, GET, or END");

        _form = Form.Get;
        var session = input.Next(ref idx);
        var file = input.Next(ref idx);
        if (session.IsEmpty || file.IsEmpty)
            return Fail(HighwayErrors.InvalidArg, "GET requires sessionId, fileName, offset, maxBytes");
        _sessionId = Encoding.UTF8.GetString(session);
        _fileName = Encoding.UTF8.GetString(file);
        if (!TryReadUInt64(input, ref idx, "offset", out _offset))
            return false;
        if (!TryReadUInt64(input, ref idx, "maxBytes", out var maxBytes))
            return false;
        if (maxBytes == 0 || maxBytes > int.MaxValue)
            return Fail(HighwayErrors.InvalidArg, "maxBytes must be in 1..2147483647");
        _maxBytes = (int)maxBytes;
        return true;
    }

    protected override void Run(CommandContext ctx, RespWriter writer)
    {
        if (!HwReplHelloCommand.TryFeeder(ctx, writer, out var feeder))
            return;

        switch (_form)
        {
            case Form.Begin:
                var began = feeder.BeginSnapshot();
                var manifest = began.Manifest.Select(f => (f.FileName, f.Size)).ToList();
                writer.SnapshotBegin(began.Epoch, began.Sequence, began.SessionId, manifest);
                break;
            case Form.Get:
                var chunk = feeder.ReadSnapshotChunk(_sessionId, _fileName, _offset, _maxBytes);
                writer.SnapshotChunk(chunk.FileName, chunk.Offset, chunk.Data, chunk.NextOffset);
                break;
            default:
                feeder.EndSnapshot(_sessionId);
                writer.SimpleString("OK");
                break;
        }
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
