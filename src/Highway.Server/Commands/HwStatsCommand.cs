using System.Buffers;
using System.Text;
using Garnet.common;
using Garnet.server;
using Highway.Server.Internal;
using Tsavorite.core;

namespace Highway.Server.Commands;

/// <summary>
/// HW.STATS [&lt;service&gt;|&lt;channel&gt;] → flat <c>[name, value, ...]</c> array.
///
/// <list type="bullet">
///   <item>no argument → <c>kind server nodes N services N channels N pendingRequests N</c></item>
///   <item>service name → <c>kind service queueDepth N hosts N inFlight N</c></item>
///   <item>channel name → <c>kind channel groups N pending N backlog N</c></item>
/// </list>
///
/// <para>A flat field/value array stays readable in <c>redis-cli</c> and
/// parseable without a schema, and extends by appending fields rather than by
/// changing shape — which matters because feature 002 builds on these replies.</para>
///
/// <para>A name that is both a service and a channel resolves as a service; the
/// <c>kind</c> field makes the resolution explicit rather than ambiguous. An
/// unknown name returns zeroed counters, never an error: an operator querying a
/// name that has seen no traffic deserves an answer, not a failure.</para>
///
/// <para><b>No snapshot consistency.</b> Counters are read under this
/// transaction's locks but describe independently-mutating structures; the reply
/// is a set of point-in-time readings, not a coherent instant. Safe to poll.</para>
/// </summary>
internal sealed class HwStatsCommand : HighwayCommandBase
{
    private readonly HighwayServerOptions _opts;

    private string? _name;
    private string[] _registeredNodes = [];
    private string[] _serviceHosts = [];
    private string[] _channelGroups = [];
    private bool _isService;

    public HwStatsCommand(HighwayServerOptions opts) => _opts = opts;

    public override bool Prepare<TGarnetReadApi>(TGarnetReadApi api, ref CustomProcedureInput procInput)
    {
        var idx = 0;
        var arg = GetNextArg(ref procInput, ref idx);

        if (arg.Length > 0)
        {
            if (!Identifier.IsValid(arg.ReadOnlySpan, _opts.MaxIdentifierBytes))
            {
                Fail(HighwayErrors.InvalidArg,
                    $"name is blank, contains a control character, or exceeds {_opts.MaxIdentifierBytes} bytes");
                return true;
            }

            _name = Encoding.UTF8.GetString(arg.ReadOnlySpan);

            // A name is a service when the discovery index knows it; otherwise
            // it is reported as a channel (zeroed when neither).
            api.GET(CreateArgSlice(HighwayKeys.RegistrationService(_name)), out PinnedSpanByte hosts);
            _serviceHosts = SplitList(hosts);
            _isService = _serviceHosts.Length > 0;

            if (_isService)
            {
                AddKey(CreateArgSlice(HighwayKeys.ServiceQueue(_name)), LockType.Shared, StoreType.Object);
                foreach (var node in _serviceHosts)
                {
                    AddKey(CreateArgSlice(HighwayKeys.ServiceProcessing(_name, node)), LockType.Shared, StoreType.Object);
                    AddKey(CreateArgSlice(HighwayKeys.RegistrationNode(node)), LockType.Shared, StoreType.Main);
                }
            }
            else
            {
                api.GET(CreateArgSlice(HighwayKeys.ChannelGroupList(_name)), out PinnedSpanByte groups);
                _channelGroups = SplitList(groups);

                AddKey(CreateArgSlice(HighwayKeys.ChannelBacklog(_name)), LockType.Shared, StoreType.Object);
                foreach (var group in _channelGroups)
                    AddKey(CreateArgSlice(HighwayKeys.GroupQueue(_name, group)), LockType.Shared, StoreType.Object);
            }

            return true;
        }

        // Server-wide form.
        api.GET(CreateArgSlice(HighwayKeys.RegistrationNodeList), out PinnedSpanByte nodes);
        _registeredNodes = SplitList(nodes);
        foreach (var node in _registeredNodes)
            AddKey(CreateArgSlice(HighwayKeys.RegistrationNode(node)), LockType.Shared, StoreType.Main);

        return true;
    }

    public override void Main<TGarnetApi>(TGarnetApi api, ref CustomProcedureInput procInput, ref MemoryResult<byte> output)
    {
        if (TryWriteError(ref output)) return;

        try
        {
            var fields = _name is null
                ? ServerStats(api)
                : _isService ? ServiceStats(api, _name) : ChannelStats(api, _name);

            WriteFieldArray(ref output, fields);
        }
        catch (Exception ex)
        {
            WriteError(ref output, HighwayErrors.InternalError(ex.Message));
        }
    }

    private List<(string Name, string Value)> ServerStats<TGarnetApi>(TGarnetApi api)
        where TGarnetApi : IGarnetApi
    {
        var now = DateTime.UtcNow.Ticks;
        var live = 0;
        var services = new HashSet<string>(StringComparer.Ordinal);
        var channels = new HashSet<string>(StringComparer.Ordinal);

        foreach (var node in _registeredNodes)
        {
            api.GET(CreateArgSlice(HighwayKeys.RegistrationNode(node)), out PinnedSpanByte record);
            if (record.Length < NodeRegistration.HeaderSize) continue;

            NodeRegistration.Decode(record.ReadOnlySpan, out var seen, out var catalog);
            if (NodeRegistration.IsStale(seen, now, _opts.NodeExpiry)) continue;

            live++;
            CatalogNames(catalog, services, channels);
        }

        return
        [
            ("kind", "server"),
            ("nodes", live.ToString()),
            ("services", services.Count.ToString()),
            ("channels", channels.Count.ToString()),
        ];
    }

    private List<(string Name, string Value)> ServiceStats<TGarnetApi>(TGarnetApi api, string service)
        where TGarnetApi : IGarnetApi
    {
        api.ListLength(CreateArgSlice(HighwayKeys.ServiceQueue(service)), out var queueDepth);

        var now = DateTime.UtcNow.Ticks;
        var hosts = 0;
        var inFlight = 0;

        foreach (var node in _serviceHosts)
        {
            api.GET(CreateArgSlice(HighwayKeys.RegistrationNode(node)), out PinnedSpanByte record);
            if (record.Length >= NodeRegistration.HeaderSize
                && !NodeRegistration.IsStale(record.ReadOnlySpan, now, _opts.NodeExpiry))
            {
                hosts++;
            }

            api.ListLength(CreateArgSlice(HighwayKeys.ServiceProcessing(service, node)), out var procLen);
            inFlight += procLen;
        }

        return
        [
            ("kind", "service"),
            ("queueDepth", queueDepth.ToString()),
            ("hosts", hosts.ToString()),
            ("inFlight", inFlight.ToString()),
        ];
    }

    private List<(string Name, string Value)> ChannelStats<TGarnetApi>(TGarnetApi api, string channel)
        where TGarnetApi : IGarnetApi
    {
        var pending = 0;
        foreach (var group in _channelGroups)
        {
            api.ListLength(CreateArgSlice(HighwayKeys.GroupQueue(channel, group)), out var len);
            pending += len;
        }

        api.ListLength(CreateArgSlice(HighwayKeys.ChannelBacklog(channel)), out var backlog);

        return
        [
            ("kind", "channel"),
            ("groups", _channelGroups.Length.ToString()),
            ("pending", pending.ToString()),
            ("backlog", backlog.ToString()),
        ];
    }

    /// <summary>Collects service and channel names from a stored catalog, ignoring malformed content.</summary>
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
            // A catalog that cannot be read contributes nothing; it was rejected
            // at registration, so this is defensive only.
        }

        static void Collect(System.Text.Json.JsonElement root, string property, HashSet<string> into)
        {
            if (!root.TryGetProperty(property, out var array)
                || array.ValueKind != System.Text.Json.JsonValueKind.Array)
            {
                return;
            }

            foreach (var entry in array.EnumerateArray())
            {
                if (entry.ValueKind == System.Text.Json.JsonValueKind.Object
                    && entry.TryGetProperty("name", out var name)
                    && name.ValueKind == System.Text.Json.JsonValueKind.String
                    && name.GetString() is { Length: > 0 } value)
                {
                    into.Add(value);
                }
            }
        }
    }

    /// <summary>Writes a flat RESP array of alternating field names and values.</summary>
    private static unsafe void WriteFieldArray(ref MemoryResult<byte> output, List<(string Name, string Value)> fields)
    {
        var encoded = new List<byte[]>(fields.Count * 2);
        foreach (var (name, value) in fields)
        {
            encoded.Add(Encoding.UTF8.GetBytes(name));
            encoded.Add(Encoding.UTF8.GetBytes(value));
        }

        var totalLen = ArrayHeaderLen(encoded.Count);
        foreach (var item in encoded)
            totalLen += BulkStringLen(item.Length);

        output.MemoryOwner?.Dispose();
        output.MemoryOwner = MemoryPool<byte>.Shared.Rent(totalLen);
        output.Length = totalLen;

        fixed (byte* ptr = output.MemoryOwner.Memory.Span)
        {
            var curr = ptr;
            var end = ptr + totalLen;
            RespWriteUtils.TryWriteArrayLength(encoded.Count, ref curr, end);
            foreach (var item in encoded)
                RespWriteUtils.TryWriteBulkString(item, ref curr, end);
        }
    }

    private static int ArrayHeaderLen(int count) => 1 + CountDigits(count) + 2;
    private static int BulkStringLen(int len) => 1 + CountDigits(len) + 2 + len + 2;

    private static int CountDigits(int value)
    {
        if (value < 0) value = -value;
        var digits = 1;
        while (value >= 10) { value /= 10; digits++; }
        return digits;
    }
}
