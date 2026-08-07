using System.Text;
using System.Text.Json;
using Garnet.common;
using Garnet.server;
using Highway.Server.Internal;
using Tsavorite.core;

namespace Highway.Server.Commands;

/// <summary>
/// HW.HEARTBEAT — three forms of one command (feature 006):
///
/// <list type="bullet">
///   <item><c>HW.HEARTBEAT &lt;nodeId&gt; &lt;catalogJson&gt;</c> → <c>+OK</c> —
///         <b>registration</b>. Stores the catalog, rebuilds this node's entries in
///         the discovery index, refreshes liveness. Sent once per node lifetime.</item>
///   <item><c>HW.HEARTBEAT &lt;nodeId&gt;</c> → <c>+OK</c> | <c>+REGISTER</c> —
///         <b>liveness</b>. Refreshes the timestamp only: no parse, no index write,
///         and the stored catalog is preserved byte-for-byte. This is the steady-state beat.</item>
///   <item><c>HW.HEARTBEAT &lt;nodeId&gt; BYE</c> → <c>+OK</c> —
///         <b>departure</b>. Runs the same teardown as dead-node pruning.</item>
/// </list>
///
/// <para><b>Why the split.</b> A node's catalog is static for its lifetime, so
/// re-sending it every beat would put up to <c>MaxCatalogBytes</c> on the wire
/// per node per interval and force a server-side JSON parse to rebuild an index
/// that never changes.</para>
///
/// <para><b>Why <c>+REGISTER</c> is correctness, not politeness.</b> Pruning
/// deletes a node's registration record <em>and</em> its index entries. A
/// liveness beat that simply recreated the timestamp would leave the node alive
/// but undiscoverable — serving a queue nobody is told about, with nothing to
/// surface the fault. Replying <c>+REGISTER</c> whenever the record is absent
/// makes a wiped registry self-healing within one beat.</para>
///
/// <para><b>Why this command never sweeps stale registrations.</b> An earlier
/// version dropped expired records here, which looked like cheap housekeeping
/// and was actively harmful: the record is the only evidence
/// <c>HW.DEQUEUE</c> uses to recognise a dead node and recover its
/// unacknowledged requests. Deleting it first left the work stranded until the
/// far slower per-entry lease sweep found it. Pruning therefore happens only
/// where the full teardown can be performed — <c>HW.DEQUEUE</c> (which locks the
/// service's queue and processing lists) and the <c>BYE</c> form (which knows
/// its own catalog). The cost is that a node whose services are never dequeued
/// again leaves one small record behind; losing in-flight work would be far
/// worse than leaking a key.</para>
/// </summary>
internal sealed class HwHeartbeatCommand : HighwayCommandBase
{
    /// <summary>Reserved second argument marking graceful departure.</summary>
    private static readonly byte[] ByeToken = "BYE"u8.ToArray();

    private enum Form { Liveness, Registration, Departure }

    private readonly HighwayServerOptions _opts;

    private string _nodeId = null!;
    private Form _form;
    private byte[] _catalog = [];
    private string[] _catalogServices = [];

    public HwHeartbeatCommand(HighwayServerOptions opts) => _opts = opts;

    public override bool Prepare<TGarnetReadApi>(TGarnetReadApi api, ref CustomProcedureInput procInput)
    {
        var idx = 0;
        if (!TryReadIdentifier(ref procInput, ref idx, "nodeId", _opts.MaxIdentifierBytes, out _nodeId))
            return true;

        var second = GetNextArg(ref procInput, ref idx);
        var raw = second.ReadOnlySpan;

        if (raw.Length == 0)
        {
            _form = Form.Liveness;

            // Liveness must stay a two-key operation: one GET + one SET on the
            // node's own record. No sweep, no index, no list scan.
            AddKey(CreateArgSlice(HighwayKeys.RegistrationNode(_nodeId)), LockType.Exclusive, StoreType.Main);
            return true;
        }

        if (raw.SequenceEqual(ByeToken))
        {
            _form = Form.Departure;
            PrepareTeardownKeys(api);
            return true;
        }

        // Registration form.
        if (raw.Length > _opts.MaxCatalogBytes)
        {
            Fail(HighwayErrors.PayloadTooLarge, $"{raw.Length} > {_opts.MaxCatalogBytes}");
            return true;
        }

        _catalog = raw.ToArray();
        if (!TryParseCatalogServices(_catalog, out _catalogServices))
        {
            Fail(HighwayErrors.InvalidArg,
                "catalog is neither the reserved token 'BYE' nor parseable catalog JSON");
            return true;
        }

        _form = Form.Registration;

        AddKey(CreateArgSlice(HighwayKeys.RegistrationNode(_nodeId)), LockType.Exclusive, StoreType.Main);
        AddKey(CreateArgSlice(HighwayKeys.RegistrationNodeList), LockType.Exclusive, StoreType.Main);

        // Lock the index for every service this node claims now, plus every
        // service it claimed before, so a changed catalog leaves nothing stale.
        foreach (var service in ServicesToLockForRegistration(api))
            AddKey(CreateArgSlice(HighwayKeys.RegistrationService(service)), LockType.Exclusive, StoreType.Main);

        return true;
    }

    public override void Main<TGarnetApi>(TGarnetApi api, ref CustomProcedureInput procInput, ref MemoryResult<byte> output)
    {
        if (TryWriteError(ref output)) return;

        try
        {
            switch (_form)
            {
                case Form.Liveness:
                    RunLiveness(api, ref output);
                    break;
                case Form.Registration:
                    RunRegistration(api, ref output);
                    break;
                default:
                    RunDeparture(api, ref output);
                    break;
            }
        }
        catch (Exception ex)
        {
            WriteError(ref output, HighwayErrors.InternalError(ex.Message));
        }
    }

    // -------------------------------------------------------------------------
    // Forms
    // -------------------------------------------------------------------------

    /// <summary>
    /// Refreshes the timestamp and nothing else. Replies <c>+REGISTER</c> —
    /// mutating nothing — when the server holds no record for this node.
    /// </summary>
    private void RunLiveness<TGarnetApi>(TGarnetApi api, ref MemoryResult<byte> output)
        where TGarnetApi : IGarnetApi
    {
        var key = CreateArgSlice(HighwayKeys.RegistrationNode(_nodeId));
        api.GET(key, out PinnedSpanByte record);

        if (record.Length == 0)
        {
            // Never registered, pruned, or the registry was lost with the server.
            // Do NOT recreate the record here: without the catalog we cannot
            // rebuild the discovery index, and a node that is live but absent
            // from the index is worse than one that is plainly gone.
            WriteSimpleString(ref output, "REGISTER");
            return;
        }

        api.SET(key, CreateArgSlice(NodeRegistration.Touch(record.ReadOnlySpan, DateTime.UtcNow.Ticks)));
        WriteSimpleString(ref output, "OK");
    }

    /// <summary>Stores the catalog verbatim and rebuilds this node's index entries.</summary>
    private void RunRegistration<TGarnetApi>(TGarnetApi api, ref MemoryResult<byte> output)
        where TGarnetApi : IGarnetApi
    {
        var key = CreateArgSlice(HighwayKeys.RegistrationNode(_nodeId));

        // Drop index entries from the previous catalog before adding the new
        // ones, so a redeployed node under the same name leaves nothing stale.
        api.GET(key, out PinnedSpanByte previous);
        if (previous.Length >= NodeRegistration.HeaderSize)
        {
            NodeRegistration.Decode(previous.ReadOnlySpan, out _, out var previousCatalog);
            if (TryParseCatalogServices(previousCatalog, out var previousServices))
            {
                foreach (var service in previousServices)
                {
                    if (Array.IndexOf(_catalogServices, service) < 0)
                        RemoveFromServiceIndex(api, service, _nodeId);
                }
            }
        }

        api.SET(key, CreateArgSlice(NodeRegistration.Encode(DateTime.UtcNow.Ticks, _catalog)));
        AddToMirrorList(api, HighwayKeys.RegistrationNodeList, _nodeId);

        foreach (var service in _catalogServices)
            AddToMirrorList(api, HighwayKeys.RegistrationService(service), _nodeId);

        WriteSimpleString(ref output, "OK");
    }

    /// <summary>
    /// Graceful departure: the same teardown a dead node gets, run immediately
    /// so operators see the node leave now rather than after the expiry window.
    /// </summary>
    private void RunDeparture<TGarnetApi>(TGarnetApi api, ref MemoryResult<byte> output)
        where TGarnetApi : IGarnetApi
    {
        foreach (var service in _catalogServices)
        {
            // Unacked work goes back to the queue; the subscriber group is NOT
            // touched — a node that shuts down cleanly still expects its
            // pending messages when it returns.
            RequeueNodeWork(api, service, _nodeId);
            RemoveNodeFromService(api, service, _nodeId);
            RemoveFromServiceIndex(api, service, _nodeId);
        }

        RemoveRegistration(api, _nodeId);
        WriteSimpleString(ref output, "OK");
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Services whose index keys must be locked for a registration: the new
    /// catalog's services plus the previous catalog's, so a changed catalog can
    /// remove what it no longer hosts.
    /// </summary>
    private IEnumerable<string> ServicesToLockForRegistration<TGarnetReadApi>(TGarnetReadApi api)
        where TGarnetReadApi : IGarnetReadApi
    {
        var services = new HashSet<string>(_catalogServices, StringComparer.Ordinal);

        api.GET(CreateArgSlice(HighwayKeys.RegistrationNode(_nodeId)), out PinnedSpanByte previous);
        if (previous.Length >= NodeRegistration.HeaderSize)
        {
            NodeRegistration.Decode(previous.ReadOnlySpan, out _, out var previousCatalog);
            if (TryParseCatalogServices(previousCatalog, out var previousServices))
            {
                foreach (var service in previousServices)
                    services.Add(service);
            }
        }

        return services;
    }

    /// <summary>
    /// Extracts service names from catalog JSON. Read-only: the stored copy is
    /// always the caller's bytes verbatim. A catalog the server cannot read is
    /// rejected rather than indexed as empty — a node whose services cannot be
    /// derived would be permanently undiscoverable, which is worse than a loud failure.
    /// </summary>
    private static bool TryParseCatalogServices(ReadOnlySpan<byte> catalog, out string[] services)
    {
        services = [];
        try
        {
            var reader = new Utf8JsonReader(catalog);
            using var document = JsonDocument.ParseValue(ref reader);

            if (!document.RootElement.TryGetProperty("services", out var array)
                || array.ValueKind != JsonValueKind.Array)
            {
                return true; // valid JSON with no services — a pure caller
            }

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

    /// <summary>
    /// Locks everything the departure teardown touches. The node's own catalog
    /// names the services, so departure can clean the index completely — unlike
    /// dead-node pruning inside <c>HW.DEQUEUE</c>, which only knows one service.
    /// </summary>
    private void PrepareTeardownKeys<TGarnetReadApi>(TGarnetReadApi api)
        where TGarnetReadApi : IGarnetReadApi
    {
        AddKey(CreateArgSlice(HighwayKeys.RegistrationNode(_nodeId)), LockType.Exclusive, StoreType.Main);
        AddKey(CreateArgSlice(HighwayKeys.RegistrationNodeList), LockType.Exclusive, StoreType.Main);

        api.GET(CreateArgSlice(HighwayKeys.RegistrationNode(_nodeId)), out PinnedSpanByte record);
        if (record.Length >= NodeRegistration.HeaderSize)
        {
            NodeRegistration.Decode(record.ReadOnlySpan, out _, out var catalog);
            TryParseCatalogServices(catalog, out _catalogServices);
        }

        foreach (var service in _catalogServices)
        {
            AddKey(CreateArgSlice(HighwayKeys.RegistrationService(service)), LockType.Exclusive, StoreType.Main);
            AddKey(CreateArgSlice(HighwayKeys.ServiceQueue(service)), LockType.Exclusive, StoreType.Object);
            AddKey(CreateArgSlice(HighwayKeys.ServiceProcessing(service, _nodeId)), LockType.Exclusive, StoreType.Object);
            AddKey(CreateArgSlice(HighwayKeys.ServiceNodes(service)), LockType.Exclusive, StoreType.Object);
            AddKey(CreateArgSlice(HighwayKeys.ServiceNodeList(service)), LockType.Exclusive, StoreType.Main);
        }
    }
}
