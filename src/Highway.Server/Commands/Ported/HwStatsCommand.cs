using System.Text;
using Highway.Server.Commands.Runtime;
using Highway.Server.Internal;
using Highway.Server.Storage;
using Highway.Server.Storage.Layout;

namespace Highway.Server.Commands.Ported;

/// <summary>
/// HW.STATS [name] → flat field/value array (feature 014/002). Reports depth/health for a
/// queue, service, channel, the recorder, or the whole server.
///
/// Ported from Garnet. The key model change: <c>ListLength</c> is now a <b>range count</b>
/// over the list prefix (038 R2.1), not an O(1) object length — behaviour identical, cost is
/// a bounded prefix scan. Read-only: no lock, no batch. Membership comes from
/// <see cref="IHighwayStore.SetMembers"/> (no mirror). The server/service/channel forms that
/// count <i>live</i> nodes read registration records (KV) — full liveness data arrives once
/// T7 ports the registration write path; the queue and recorder forms are complete here (the
/// T6 parity targets).
/// </summary>
internal sealed class HwStatsCommand : HighwayCommand
{
    private const string QueuePrefix = "Q:";
    private const string RecorderForm = "RECORDER";

    private string? _name;
    private string? _queueName;
    private bool _isRecorder;

    protected override bool Parse(CommandContext ctx, CommandInput input)
    {
        var idx = 0;
        var arg = input.Next(ref idx);
        if (arg.Length == 0)
            return true; // server-wide form

        if (!Identifier.IsValidAllowingAt(arg, ctx.Options.MaxIdentifierBytes))
            return Fail(HighwayErrors.InvalidArg,
                $"name is blank, contains a control character, or exceeds {ctx.Options.MaxIdentifierBytes} bytes");

        _name = Encoding.UTF8.GetString(arg);

        if (string.Equals(_name, RecorderForm, StringComparison.OrdinalIgnoreCase))
        {
            _isRecorder = true;
            return true;
        }

        if (_name.StartsWith(QueuePrefix, StringComparison.OrdinalIgnoreCase))
        {
            _queueName = _name[QueuePrefix.Length..];
            if (_queueName.Length == 0)
                return Fail(HighwayErrors.InvalidArg, "HW.STATS Q: requires a queue name");
        }

        return true;
    }

    protected override void Run(CommandContext ctx, RespWriter writer)
    {
        using var snap = ctx.Store.Snapshot();

        var fields =
            _isRecorder ? RecorderStats(ctx)
            : _queueName is not null ? QueueStats(ctx, snap, _queueName)
            : _name is not null ? NamedStats(ctx, snap, _name)
            : ServerStats(ctx, snap);

        writer.FieldArray(fields);
    }

    private static List<(string, string)> QueueStats(CommandContext ctx, IStoreSnapshot snap, string queue)
    {
        var depth = ctx.Store.ListLength(snap, HighwayKeyspace.ListPrefix(HighwayNames.Queue(queue)));
        var deadLettered = ctx.Store.ListLength(snap, HighwayKeyspace.ListPrefix(HighwayNames.QueueDeadLetter(queue)));
        var deferred = ctx.Store.SortedSetLength(snap, HighwayKeyspace.SortedSetPrefix(HighwayNames.QueueDelayed(queue)));

        var workers = ctx.Store.SetMembers(snap, HighwayKeyspace.SetPrefix(HighwayNames.QueueNodes(queue)));
        long inFlight = 0;
        foreach (var w in workers)
        {
            var node = Encoding.UTF8.GetString(w);
            inFlight += ctx.Store.ListLength(snap, HighwayKeyspace.ListPrefix(HighwayNames.QueueProcessing(queue, node)));
        }

        return
        [
            ("kind", "queue"),
            ("depth", depth.ToString()),
            ("workers", workers.Count.ToString()),
            ("inFlight", inFlight.ToString()),
            ("deferred", deferred.ToString()),
            ("deadLettered", deadLettered.ToString()),
        ];
    }

    private static List<(string, string)> RecorderStats(CommandContext ctx)
    {
        var s = ctx.Recorder.Snapshot();
        return
        [
            ("kind", "recorder"),
            ("enabled", s.Enabled ? "1" : "0"),
            ("names", s.Names.ToString()),
            ("events", s.Events.ToString()),
            ("bytes", s.Bytes.ToString()),
            ("droppedCapacity", s.DroppedCapacity.ToString()),
            ("droppedBudget", s.DroppedBudget.ToString()),
            ("failures", s.Failures.ToString()),
            ("observerFailures", s.ObserverFailures.ToString()),
        ];
    }

    /// <summary>
    /// A named entity that is not a queue: report it as a service if the discovery index
    /// knows it, otherwise as a channel. Depth/health from the store; live-node counting is
    /// completed when T7 ports registration writes.
    /// </summary>
    private static List<(string, string)> NamedStats(CommandContext ctx, IStoreSnapshot snap, string name)
    {
        var serviceHosts = ctx.Store.SetMembers(snap, HighwayKeyspace.SetPrefix(HighwayNames.RegistrationService(name)));
        if (serviceHosts.Count > 0)
        {
            var queueDepth = ctx.Store.ListLength(snap, HighwayKeyspace.ListPrefix(HighwayNames.ServiceQueue(name)));
            var deadLettered = ctx.Store.ListLength(snap, HighwayKeyspace.ListPrefix(HighwayNames.ServiceDeadLetter(name)));
            long inFlight = 0;
            foreach (var h in serviceHosts)
            {
                var node = Encoding.UTF8.GetString(h);
                inFlight += ctx.Store.ListLength(snap, HighwayKeyspace.ListPrefix(HighwayNames.ServiceProcessing(name, node)));
            }
            return
            [
                ("kind", "service"),
                ("queueDepth", queueDepth.ToString()),
                ("hosts", serviceHosts.Count.ToString()),
                ("inFlight", inFlight.ToString()),
                ("deadLettered", deadLettered.ToString()),
            ];
        }

        // Channel form: sum backlog across its groups' derived queues.
        var groups = ctx.Store.SetMembers(snap, HighwayKeyspace.SetPrefix(HighwayNames.ChannelGroups(name)));
        long pending = 0, dead = 0;
        foreach (var g in groups)
        {
            var derived = $"{name}@{Encoding.UTF8.GetString(g)}";
            pending += ctx.Store.ListLength(snap, HighwayKeyspace.ListPrefix(HighwayNames.Queue(derived)));
            dead += ctx.Store.ListLength(snap, HighwayKeyspace.ListPrefix(HighwayNames.QueueDeadLetter(derived)));
        }
        return
        [
            ("kind", "channel"),
            ("groups", groups.Count.ToString()),
            ("pending", pending.ToString()),
            ("deadLettered", dead.ToString()),
        ];
    }

    private static List<(string, string)> ServerStats(CommandContext ctx, IStoreSnapshot snap)
    {
        // Parity with the Garnet ServerStats (found by the 040 fixture swap): "nodes" counts
        // LIVE registrations only (a stale node is not a node), and the reply carries the
        // distinct service/channel counts read from each live node's catalog.
        var live = 0;
        var services = new HashSet<string>(StringComparer.Ordinal);
        var channels = new HashSet<string>(StringComparer.Ordinal);

        foreach (var member in ctx.Store.SetMembers(snap, HighwayKeyspace.SetPrefix(HighwayNames.RegistrationNodeList)))
        {
            var node = Encoding.UTF8.GetString(member);
            var record = ctx.Store.Get(snap, HighwayKeyspace.Kv(HighwayNames.RegistrationNode(node)));
            if (record is null || record.Length < NodeRegistration.HeaderSize) continue;

            NodeRegistration.Decode(record, out var seen, out var catalog);
            if (NodeRegistration.IsStale(seen, ctx.NowTicks, ctx.Options.NodeExpiry)) continue;

            live++;
            CatalogNames(catalog, services, channels);
        }

        return
        [
            ("kind", "server"),
            ("nodes", live.ToString()),
            ("services", services.Count.ToString()),
            ("channels", channels.Count.ToString()),
            ("groupsRetired", ctx.Recorder.GroupsRetired.ToString()),
            ("messagesDiscarded", ctx.Recorder.MessagesDiscarded.ToString()),
            ("sendsRefused", ctx.Recorder.SendsRefused.ToString()),
        ];
    }

    /// <summary>Collects service and channel names from a stored catalog, ignoring malformed content (the Garnet command's reader, verbatim in behavior).</summary>
    private static void CatalogNames(ReadOnlySpan<byte> catalog, HashSet<string> services, HashSet<string> channels)
    {
        try
        {
            var reader = new System.Text.Json.Utf8JsonReader(catalog);
            using var document = System.Text.Json.JsonDocument.ParseValue(ref reader);
            Collect(document.RootElement, "services", services);
            Collect(document.RootElement, "channels", channels);
        }
        catch (System.Text.Json.JsonException)
        {
            // Rejected at registration already; defensive only.
        }

        static void Collect(System.Text.Json.JsonElement root, string property, HashSet<string> into)
        {
            if (!root.TryGetProperty(property, out var array)
                || array.ValueKind != System.Text.Json.JsonValueKind.Array)
            {
                return;
            }

            foreach (var element in array.EnumerateArray())
            {
                if (element.ValueKind == System.Text.Json.JsonValueKind.Object
                    && element.TryGetProperty("name", out var name)
                    && name.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    into.Add(name.GetString()!);
                }
            }
        }
    }
}
