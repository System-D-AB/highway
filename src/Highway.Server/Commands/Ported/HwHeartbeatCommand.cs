using System.Text;
using System.Text.Json;
using Highway.Server.Commands.Runtime;
using Highway.Server.Internal;
using Highway.Abstractions.Observability;
using Highway.Server.Storage;
using Highway.Server.Storage.Layout;

namespace Highway.Server.Commands.Ported;

/// <summary>
/// HW.HEARTBEAT — three forms of one command (feature 006), plus BYE PURGE (017):
///
/// <list type="bullet">
///   <item><c>HW.HEARTBEAT &lt;nodeId&gt; &lt;catalogJson&gt;</c> → <c>+OK</c> — <b>registration</b>:
///         store the catalog, rebuild the discovery index entries, refresh liveness.</item>
///   <item><c>HW.HEARTBEAT &lt;nodeId&gt;</c> → <c>+OK</c> | <c>+REGISTER</c> — <b>liveness</b>:
///         refresh the timestamp only; <c>+REGISTER</c> when no record exists (self-healing).</item>
///   <item><c>HW.HEARTBEAT &lt;nodeId&gt; BYE</c> → <c>+OK</c> — <b>departure</b>: the same
///         teardown a dead node gets, run now.</item>
///   <item><c>HW.HEARTBEAT &lt;nodeId&gt; BYE PURGE</c> → <c>*3</c> [groups, msgs, bytes] —
///         departure plus subscriber-group retirement (never coming back).</item>
/// </list>
///
/// <para>Ported from Garnet. The clock is <see cref="CommandContext.NowTicks"/> (037 R5.1), and
/// every mirror the forms read — <c>reg:nodes</c>, <c>reg:svc:{s}</c>, <c>svc:{s}:nodes</c>,
/// <c>node:channels</c>, <c>node:subs</c>, <c>grp:members</c> — is a plain set now (T3.3). The
/// four forms are the whole of the node lifecycle, so this command completes the registry port
/// that discover/dequeue/qclaim read from.</para>
/// </summary>
internal sealed class HwHeartbeatCommand : HighwayCommand
{
    private enum Form { Liveness, Registration, Departure, Purge }

    private string _nodeId = null!;
    private Form _form;
    private byte[] _catalog = [];
    private string[] _catalogServices = [];

    // Purge accounting for the recorder.
    private RegistrySupport.RetirementOutcome _retired;

    protected override bool Parse(CommandContext ctx, CommandInput input)
    {
        var idx = 0;
        if (!TryReadIdentifier(input, ref idx, "nodeId", ctx.Options.MaxIdentifierBytes, out _nodeId))
            return false;

        var second = input.Next(ref idx);

        if (second.Length == 0)
        {
            _form = Form.Liveness;
            return true;
        }

        if (Encoding.ASCII.GetString(second).Equals("BYE", StringComparison.Ordinal))
        {
            var third = input.Next(ref idx);
            _form = third.Length > 0 && Encoding.ASCII.GetString(third).Equals("PURGE", StringComparison.Ordinal)
                ? Form.Purge
                : Form.Departure;
            return true;
        }

        // Registration form.
        if (second.Length > ctx.Options.MaxCatalogBytes)
            return Fail(HighwayErrors.PayloadTooLarge, $"{second.Length} > {ctx.Options.MaxCatalogBytes}");

        _catalog = second.ToArray();
        if (!TryParseCatalogServices(_catalog, out _catalogServices))
            return Fail(HighwayErrors.InvalidArg,
                "catalog is neither the reserved token 'BYE' nor parseable catalog JSON");

        _form = Form.Registration;
        return true;
    }

    protected override void Run(CommandContext ctx, RespWriter writer)
    {
        var store = ctx.Store;

        // One per-name lock on the node id serializes this node's lifecycle (037 §3). Every
        // form mutates keys rooted at this node.
        using var _lock = ctx.Locks.Lock(_nodeId);
        using var snap = store.Snapshot();
        using var batch = store.NewBatch();

        switch (_form)
        {
            case Form.Liveness: RunLiveness(ctx, store, snap, batch, writer); break;
            case Form.Registration: RunRegistration(ctx, store, snap, batch, writer); break;
            case Form.Departure: RunDeparture(store, snap, batch, writer); break;
            default: RunPurge(store, snap, batch, writer); break;
        }
    }

    // -------------------------------------------------------------------------
    // Forms
    // -------------------------------------------------------------------------

    private void RunLiveness(CommandContext ctx, IHighwayStore store, IStoreSnapshot snap, IStoreBatch batch, RespWriter writer)
    {
        var key = HighwayKeyspace.Kv(HighwayNames.RegistrationNode(_nodeId));
        var record = store.Get(snap, key);

        if (record is null || record.Length == 0)
        {
            // Never registered, pruned, or the registry was lost. Do NOT recreate the record:
            // without the catalog we cannot rebuild the discovery index, and a node that is
            // live but absent from the index is worse than one that is plainly gone.
            batch.Commit(); // nothing staged, but keeps the shape uniform
            writer.SimpleString("REGISTER");
            return;
        }

        // 060: a beat refreshes an 8-byte liveness key; the registration record (and its catalogue) is
        // left untouched. Before this, every beat re-encoded and re-wrote the whole catalogue — a synced
        // write, replicated to every standby — to change 8 bytes of it.
        RegistrySupport.TouchSeen(store, batch, _nodeId, ctx.NowTicks);
        batch.Commit();
        writer.SimpleString("OK");
    }

    private void RunRegistration(CommandContext ctx, IHighwayStore store, IStoreSnapshot snap, IStoreBatch batch, RespWriter writer)
    {
        var key = HighwayKeyspace.Kv(HighwayNames.RegistrationNode(_nodeId));

        // Drop index entries from the previous catalog before adding the new ones, so a
        // redeployed node under the same name leaves nothing stale.
        var previous = store.Get(snap, key);
        if (previous is not null && previous.Length >= NodeRegistration.HeaderSize)
        {
            NodeRegistration.Decode(previous, out _, out var previousCatalog);
            if (TryParseCatalogServices(previousCatalog.ToArray(), out var previousServices))
            {
                foreach (var service in previousServices)
                {
                    if (Array.IndexOf(_catalogServices, service) < 0)
                        RegistrySupport.RemoveFromServiceIndex(store, batch, service, _nodeId);
                }
            }
        }

        store.Set(batch, key, NodeRegistration.Encode(ctx.NowTicks, _catalog));
        RegistrySupport.TouchSeen(store, batch, _nodeId, ctx.NowTicks);   // 060: liveness key agrees with the record
        RegistrySupport.SetAdd(store, batch, HighwayNames.RegistrationNodeList, _nodeId);

        foreach (var service in _catalogServices)
            RegistrySupport.SetAdd(store, batch, HighwayNames.RegistrationService(service), _nodeId);

        batch.Commit();
        writer.SimpleString("OK");
    }

    private void RunDeparture(IHighwayStore store, IStoreSnapshot snap, IStoreBatch batch, RespWriter writer)
    {
        _catalogServices = ReadStoredCatalogServices(store, snap);

        foreach (var service in _catalogServices)
        {
            // Unacked work goes back to the queue; subscriber groups are NOT touched — a node
            // that shuts down cleanly still expects its pending messages when it returns.
            RegistrySupport.RequeueNodeWork(store, batch, service, _nodeId);
            RegistrySupport.RemoveNodeFromService(store, batch, service, _nodeId);
            RegistrySupport.RemoveFromServiceIndex(store, batch, service, _nodeId);
        }

        RegistrySupport.RemoveRegistration(store, batch, _nodeId);

        batch.Commit();
        writer.SimpleString("OK");
    }

    private void RunPurge(IHighwayStore store, IStoreSnapshot snap, IStoreBatch batch, RespWriter writer)
    {
        _catalogServices = ReadStoredCatalogServices(store, snap);

        foreach (var service in _catalogServices)
        {
            RegistrySupport.RequeueNodeWork(store, batch, service, _nodeId);
            RegistrySupport.RemoveNodeFromService(store, batch, service, _nodeId);
            RegistrySupport.RemoveFromServiceIndex(store, batch, service, _nodeId);
        }

        var destroyed = default(RegistrySupport.RetirementOutcome);
        foreach (var (channel, group) in PurgeTargets(store, snap))
        {
            // 025: a shared group outlives any one member. Remove this node's membership;
            // destroy the queue only when it was the LAST member. The default deployment
            // (group == node, membership == {node}) takes the destroy path every time.
            var members = RegistrySupport.ReadSet(store, snap, HighwayNames.GroupMembers(channel, group));
            var remaining = members.Where(m => m != _nodeId).ToArray();

            if (members.Length > 0 && remaining.Length > 0)
            {
                // Not the last member: the group lives on for the siblings.
                RegistrySupport.SetRemove(store, batch, HighwayNames.GroupMembers(channel, group), _nodeId);
            }
            else
            {
                destroyed += RegistrySupport.RetireGroup(store, snap, batch, channel, group);
            }
        }

        store.DeleteRange(batch, HighwayKeyspace.SetPrefix(HighwayNames.NodeChannels(_nodeId)));
        store.DeleteRange(batch, HighwayKeyspace.SetPrefix(HighwayNames.NodeSubs(_nodeId)));
        RegistrySupport.RemoveRegistration(store, batch, _nodeId);

        batch.Commit();
        _retired = destroyed;

        // Returns what it destroyed, so an irreversible operation leaves a record even when
        // nobody was watching the log.
        writer.ThreeIntegers(destroyed.Groups, destroyed.Messages, destroyed.Bytes);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>Reads the stored catalog's services from the node's registration record (for BYE/PURGE).</summary>
    private string[] ReadStoredCatalogServices(IHighwayStore store, IStoreSnapshot snap)
    {
        var record = store.Get(snap, HighwayKeyspace.Kv(HighwayNames.RegistrationNode(_nodeId)));
        if (record is null || record.Length < NodeRegistration.HeaderSize)
            return [];

        NodeRegistration.Decode(record, out _, out var catalog);
        return TryParseCatalogServices(catalog.ToArray(), out var services) ? services : [];
    }

    /// <summary>
    /// The (channel, group) pairs a purge touches (025) — the union of both indexes. NodeSubs
    /// carries <c>{channel}@{group}</c> entries and is the authority; NodeChannels is the
    /// pre-025 index where the group was always named after the node, kept so a broker upgraded
    /// mid-flight still purges what old subscribes recorded.
    /// </summary>
    private (string Channel, string Group)[] PurgeTargets(IHighwayStore store, IStoreSnapshot snap)
    {
        var targets = new Dictionary<string, (string Channel, string Group)>(StringComparer.Ordinal);

        foreach (var channel in RegistrySupport.ReadNodeChannels(store, snap, _nodeId))
            targets[$"{channel}@{_nodeId}"] = (channel, _nodeId);

        foreach (var entry in RegistrySupport.ReadSet(store, snap, HighwayNames.NodeSubs(_nodeId)))
        {
            // '@' cannot appear in a channel or group name (018), so the FIRST '@' is the only
            // '@' and the split is unambiguous.
            var at = entry.IndexOf('@');
            if (at > 0 && at < entry.Length - 1)
                targets[entry] = (entry[..at], entry[(at + 1)..]);
        }

        return [.. targets.Values];
    }

    /// <summary>Extracts service names from catalog JSON. False only on JSON that cannot be parsed.</summary>
    private static bool TryParseCatalogServices(ReadOnlySpan<byte> catalog, out string[] services)
    {
        services = [];
        try
        {
            var reader = new Utf8JsonReader(catalog);
            using var document = JsonDocument.ParseValue(ref reader);

            if (!document.RootElement.TryGetProperty("services", out var array)
                || array.ValueKind != JsonValueKind.Array)
                return true; // valid JSON with no services — a pure caller

            var names = new List<string>();
            foreach (var entry in array.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.Object
                    && entry.TryGetProperty("name", out var name)
                    && name.ValueKind == JsonValueKind.String
                    && name.GetString() is { Length: > 0 } value)
                {
                    names.Add(value);
                }
            }

            services = [.. names];
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    protected override void AfterCommit(CommandContext ctx)
    {
        // Liveness beats are deliberately not recorded (006: they fire every few seconds).
        if (_form == Form.Liveness) return;

        // A retirement is a retirement however it was asked for (017 R3.4).
        if (_form == Form.Purge && _retired.Groups > 0)
        {
            ctx.Recorder.Record(
                HighwayEventType.GroupRetired, _nodeId,
                nodeId: _nodeId,
                count: (int)_retired.Messages,
                errorCode: $"retired {_retired.Groups} group(s), discarded {_retired.Messages} " +
                           $"message(s) / {_retired.Bytes} byte(s) by explicit BYE PURGE");
        }

        ctx.Recorder.Record(
            _form is Form.Departure or Form.Purge
                ? HighwayEventType.NodeDeparted
                : HighwayEventType.NodeRegistered,
            _nodeId,
            nodeId: _nodeId,
            errorCode: FailureCode);
    }
}
