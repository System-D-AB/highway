using System.Buffers;
using System.Globalization;
using System.Text;
using Garnet.common;
using Garnet.server;
using Highway.Abstractions.Observability;
using Highway.Abstractions.Scheduling;
using Highway.Server.Internal;
using Highway.Server.Observability;
using Tsavorite.core;

namespace Highway.Server.Commands;

/// <summary>
/// HW.JOB SET &lt;queue&gt; &lt;job&gt; &lt;expression&gt; &lt;template&gt; → +OK
/// HW.JOB DEL &lt;queue&gt; &lt;job&gt;                              → :1 | :0
/// HW.JOB LIST                                                → [[queue, job, expr, nextFireTicks, lastFireTicks], ...]
///
/// <para>Recurring-job schedule management (feature 028). A schedule is a member of the
/// per-queue sorted set <c>hw:job:{queue}:schedules</c> scored by <c>nextFireTicks</c>;
/// firing happens in <c>HW.QCLAIM</c>'s promotion sweep, not here.</para>
///
/// <para><b>SET is last-registration-wins, loudly</b> (OD5): re-registering an existing job
/// replaces its expression and template, preserves <c>lastFire</c>, and records
/// <c>JobScheduleChanged</c> naming both expressions when they differ — the catalog's rule,
/// which is what makes a rolling deploy converge on the new schedule.</para>
/// </summary>
internal sealed class HwJobCommand : HighwayCommandBase
{
    private static readonly byte[] SetToken = "SET"u8.ToArray();
    private static readonly byte[] DelToken = "DEL"u8.ToArray();
    private static readonly byte[] ListToken = "LIST"u8.ToArray();

    private enum Form { Set, Del, List }

    private readonly HighwayServerOptions _opts;
    private readonly FlightRecorder _recorder;

    private Form _form;
    private string _queue = null!;
    private string _job = null!;
    private string _expression = null!;
    private JobExpression _parsed = null!;
    private byte[] _template = [];
    private string[] _listQueues = [];
    private string? _changeDetail;
    private bool _removed;

    public HwJobCommand(HighwayServerOptions opts, FlightRecorder recorder)
    {
        _opts = opts;
        _recorder = recorder;
    }

    protected override void ResetState()
    {
        _listQueues = [];
        _changeDetail = null;
        _removed = false;
    }

    protected override bool PrepareCore<TGarnetReadApi>(TGarnetReadApi api, ref CustomProcedureInput procInput)
    {
        var idx = 0;
        var sub = GetNextArg(ref procInput, ref idx);

        if (sub.ReadOnlySpan.SequenceEqual(ListToken))
        {
            _form = Form.List;

            // Enumerate via the index mirror, then declare each queue's schedule set so Main
            // may read it — the BYE PURGE pattern. The index key is DECLARED BEFORE the read:
            // a Prepare-phase GET on an undeclared key returns empty here (found by probe).
            AddKey(CreateArgSlice(HighwayKeys.JobIndex), LockType.Shared, StoreType.Main);
            api.GET(CreateArgSlice(HighwayKeys.JobIndex), out PinnedSpanByte raw);
            _listQueues = raw.Length > 0
                ? Encoding.UTF8.GetString(raw.ReadOnlySpan).Split('\n', StringSplitOptions.RemoveEmptyEntries)
                : [];

            foreach (var queue in _listQueues)
                AddKey(CreateArgSlice(HighwayKeys.JobSchedules(queue)), LockType.Exclusive, StoreType.Object);

            return true;
        }

        var isSet = sub.ReadOnlySpan.SequenceEqual(SetToken);
        if (!isSet && !sub.ReadOnlySpan.SequenceEqual(DelToken))
        {
            // Fail(...) returns false; Prepare must still return TRUE so Main writes the
            // CLASSIFIED error — returning false aborts as a bare transient "Transaction
            // failed", which tells a client to retry a permanently bad command.
            Fail(HighwayErrors.InvalidArg, "first argument must be SET, DEL or LIST");
            return true;
        }

        _form = isSet ? Form.Set : Form.Del;

        if (!TryReadIdentifier(ref procInput, ref idx, "queue", _opts.MaxIdentifierBytes, out _queue))
            return true;
        if (!TryReadIdentifier(ref procInput, ref idx, "job", _opts.MaxIdentifierBytes, out _job))
            return true;

        if (isSet)
        {
            var exprArg = GetNextArg(ref procInput, ref idx);
            _expression = Encoding.UTF8.GetString(exprArg.ReadOnlySpan);

            // Validated HERE so a bad expression is a permanent, classified error before any
            // key is touched — and the message teaches the grammar (R1.7).
            if (!JobExpression.TryParse(_expression, out _parsed!, out var reason))
            {
                Fail(HighwayErrors.InvalidArg,
                    $"schedule expression '{_expression}': {reason}. Accepted: {JobExpression.AcceptedForms}");
                return true;
            }

            var template = GetNextArg(ref procInput, ref idx);
            if (template.Length == 0)
            {
                Fail(HighwayErrors.InvalidArg, "a template payload is required (the occurrence message's bytes)");
                return true;
            }
            if (template.Length > _opts.MaxPayloadBytes)
            {
                Fail(HighwayErrors.PayloadTooLarge, $"{template.Length} > {_opts.MaxPayloadBytes}");
                return true;
            }

            _template = template.ReadOnlySpan.ToArray();
        }

        AddKey(CreateArgSlice(HighwayKeys.JobSchedules(_queue)), LockType.Exclusive, StoreType.Object);
        AddKey(CreateArgSlice(HighwayKeys.JobIndex), LockType.Exclusive, StoreType.Main);
        return true;
    }

    public override void Main<TGarnetApi>(TGarnetApi api, ref CustomProcedureInput procInput, ref MemoryResult<byte> output)
    {
        if (TryWriteError(ref output)) return;

        try
        {
            switch (_form)
            {
                case Form.Set: RunSet(api, ref output); break;
                case Form.Del: RunDel(api, ref output); break;
                default: RunList(api, ref output); break;
            }
        }
        catch (Exception ex)
        {
            WriteError(ref output, HighwayErrors.InternalError(ex.Message));
        }
    }

    private void RunSet<TGarnetApi>(TGarnetApi api, ref MemoryResult<byte> output)
        where TGarnetApi : IGarnetApi
    {
        var schedKey = CreateArgSlice(HighwayKeys.JobSchedules(_queue));

        // Find an existing member for this job name; preserve its lastFire across the update.
        long lastFire = 0;
        if (FindMember(api, schedKey, _job, out var existing))
        {
            JobScheduleRecord.Decode(existing, out _, out var oldExpr, out lastFire, out _, out _);
            api.SortedSetRemove(schedKey, CreateArgSlice(existing), out _);

            if (!string.Equals(oldExpr, _expression, StringComparison.Ordinal))
                _changeDetail = $"{oldExpr} => {_expression}";
        }
        else
        {
            _changeDetail = $"registered {_expression}";
        }

        var next = _parsed.NextOccurrence(DateTime.UtcNow).Ticks;
        var record = JobScheduleRecord.Encode(_job, _expression, lastFire, next, _template);

        api.SortedSetAdd(
            schedKey,
            CreateArgSlice(next.ToString(CultureInfo.InvariantCulture)),
            CreateArgSlice(record),
            out _);

        AddToMirrorList(api, HighwayKeys.JobIndex, _queue);

        WriteSimpleString(ref output, "OK");
    }

    private void RunDel<TGarnetApi>(TGarnetApi api, ref MemoryResult<byte> output)
        where TGarnetApi : IGarnetApi
    {
        var schedKey = CreateArgSlice(HighwayKeys.JobSchedules(_queue));

        _removed = FindMember(api, schedKey, _job, out var existing);
        if (_removed)
        {
            api.SortedSetRemove(schedKey, CreateArgSlice(existing), out _);

            api.SortedSetLength(schedKey, out var remaining);
            if (remaining == 0)
                RemoveFromMirrorList(api, HighwayKeys.JobIndex, _queue);
        }

        WriteInteger(ref output, _removed ? 1 : 0);
    }

    private unsafe void RunList<TGarnetApi>(TGarnetApi api, ref MemoryResult<byte> output)
        where TGarnetApi : IGarnetApi
    {
        var rows = new List<List<byte[]>>();


        foreach (var queue in _listQueues)
        {
            var schedKey = CreateArgSlice(HighwayKeys.JobSchedules(queue));
            foreach (var member in RangeMembers(api, schedKey))
            {
                JobScheduleRecord.Decode(member, out var job, out var expr, out var lastFire, out var nextFire, out _);
                rows.Add(
                [
                    Encoding.UTF8.GetBytes(queue),
                    Encoding.UTF8.GetBytes(job),
                    Encoding.UTF8.GetBytes(expr),
                    Encoding.UTF8.GetBytes(nextFire.ToString(CultureInfo.InvariantCulture)),
                    Encoding.UTF8.GetBytes(lastFire.ToString(CultureInfo.InvariantCulture)),
                ]);
            }
        }

        var totalLen = ArrayHeaderLen(rows.Count);
        foreach (var fields in rows)
        {
            totalLen += ArrayHeaderLen(fields.Count);
            foreach (var f in fields) totalLen += BulkStringLen(f.Length);
        }

        output.MemoryOwner?.Dispose();
        output.MemoryOwner = MemoryPool<byte>.Shared.Rent(totalLen);
        output.Length = totalLen;

        fixed (byte* ptr = output.MemoryOwner.Memory.Span)
        {
            var curr = ptr;
            var end = ptr + totalLen;
            RespWriteUtils.TryWriteArrayLength(rows.Count, ref curr, end);
            foreach (var fields in rows)
            {
                RespWriteUtils.TryWriteArrayLength(fields.Count, ref curr, end);
                foreach (var f in fields)
                    RespWriteUtils.TryWriteBulkString(f, ref curr, end);
            }
        }
    }

    /// <summary>
    /// Scans the (small, topology-bounded) schedule set for a job by name. One record per
    /// declared job — this is a walk over single digits, not a query problem.
    /// </summary>
    private bool FindMember<TGarnetApi>(
        TGarnetApi api, PinnedSpanByte schedKey, string job, out byte[] member)
        where TGarnetApi : IGarnetApi
    {
        foreach (var candidate in RangeMembers(api, schedKey))
        {
            if (JobScheduleRecord.PeekName(candidate) == job)
            {
                member = candidate;
                return true;
            }
        }

        member = [];
        return false;
    }

    private IEnumerable<byte[]> RangeMembers<TGarnetApi>(TGarnetApi api, PinnedSpanByte schedKey)
        where TGarnetApi : IGarnetApi
    {
        // Two probe-found facts shape this call: `limit: default` means COUNT 0 (nothing),
        // and `withScores: true` returns members only in this API path -- which is why the
        // record carries its own nextFire instead of relying on the score for reads.
        var status = api.SortedSetRange(
            schedKey,
            CreateArgSlice("-inf"),
            CreateArgSlice("+inf"),
            SortedSetOrderOperation.ByScore,
            out var entries,
            out _,
            withScores: false,
            reverse: false,
            limit: ("0", 1024));

        if (status != GarnetStatus.OK || entries is null) yield break;

        foreach (var entry in entries)
            yield return entry.ReadOnlySpan.ToArray();
    }

    private static int ArrayHeaderLen(int count) => 1 + count.ToString(CultureInfo.InvariantCulture).Length + 2;

    private static int BulkStringLen(int len) => 1 + len.ToString(CultureInfo.InvariantCulture).Length + 2 + len + 2;

    public override void Finalize<TGarnetApi>(TGarnetApi api, ref CustomProcedureInput procInput, ref MemoryResult<byte> output)
    {
        if (Failed) return;

        switch (_form)
        {
            case Form.Set when _changeDetail is not null:
                _recorder.Record(
                    HighwayEventType.JobScheduleChanged, _queue ?? "?",
                    requestId: _job,
                    errorCode: _changeDetail);
                break;

            case Form.Del when _removed:
                _recorder.Record(
                    HighwayEventType.JobScheduleRemoved, _queue ?? "?",
                    requestId: _job);
                break;
        }
    }
}
