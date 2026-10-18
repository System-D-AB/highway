using Highway.Server.Storage.Crystal;
using System.Diagnostics;
using System.Text;
using Highway.Server.Commands.Ported;
using Highway.Server.Storage;
using Highway.Server.Storage.Replication;
using Highway.Server.Tests.Commands;
using Xunit;
using Xunit.Abstractions;

namespace Highway.Server.Tests.Storage;

/// <summary>
/// The poll-cost probe (061's reproduction, kept as a tool; 076 R7.1): real commands on a real store, nothing mocked.
/// Off by default — set <c>HW_PROBE_POLLCOST=1</c> (calls: <c>HW_PROBE_CALLS</c>, default 100 000; report file:
/// <c>HW_PROBE_OUT</c>).
/// <list type="bullet">
/// <item>RPC: CALL → CLAIM SVC → REPLY TOKEN → RESULT → ACK with 8 calls in flight, plus one idle claim per call.
/// The average claim cost is reported at checkpoints; it must stay flat with uptime (C4.9).</item>
/// <item>Then p50/p99 of each verb, and of SEND → CLAIM Q → ACK and PUBLISH → CLAIM Q → ACK.</item>
/// </list>
/// </summary>
public class PollCostProbe(ITestOutputHelper output)
{
    /// <summary>The store under test — the one line a storage engine change touches.</summary>
    private static IHighwayStore OpenStore(string dir) => CrystalHighwayStore.Open(dir);

    private const int InFlight = 8;
    private static readonly int[] Checkpoints = [2_000, 10_000, 25_000, 50_000, 100_000, 200_000];

    [Fact]
    public void PollCost_StaysFlat_AndVerbLatencies()
    {
        if (Environment.GetEnvironmentVariable("HW_PROBE_POLLCOST") != "1") return;
        var calls = int.TryParse(Environment.GetEnvironmentVariable("HW_PROBE_CALLS"), out var n) && n > 0 ? n : 100_000;

        var report = new StringBuilder();
        void Line(string s) { report.AppendLine(s); output.WriteLine(s); }

        var dir = TempDirs.NewTempDir();
        try
        {
            var store = OpenStore(dir);
            using var h = new CommandHarness(store);
            var now = DateTime.UtcNow.Ticks;
            long Tick() => now += TimeSpan.TicksPerMillisecond;

            var call = new HwCallCommand(); var claim = new HwClaimCommand(); var reply = new HwReplyCommand();
            var result = new HwResultCommand(); var ack = new HwAckCommand(); var send = new HwSendCommand();
            var publish = new HwPublishCommand();

            var tCall = new List<double>(calls); var tClaim = new List<double>(calls); var tIdle = new List<double>(calls);
            var tReply = new List<double>(calls); var tResult = new List<double>(calls); var tAck = new List<double>(calls);
            var sw = new Stopwatch();
            double Time(Action a) { sw.Restart(); a(); return sw.Elapsed.TotalMilliseconds; }

            Line($"poll-cost probe: {calls} calls, {InFlight} in flight, store {store.GetType().Name}");
            Line("calls\tavg claim ms (window)\tavg idle claim ms (window)");

            var inflight = new Queue<string>();
            var next = 0; var done = 0; var windowStart = 0; var cp = 0;
            while (done < calls)
            {
                while (inflight.Count < InFlight && next < calls)
                {
                    var id = $"r-{next++}";
                    tCall.Add(Time(() => h.Run(call, Tick(), "probe", id, "payload")));
                    inflight.Enqueue(id);
                }

                CommandHarness.Claim? row = null;
                tClaim.Add(Time(() => row = h.ClaimOne("SVC", "probe", "worker-1", Tick())));
                if (row is null) throw new InvalidOperationException($"nothing claimed at call {done}");
                var token = row.Value.Token; var rid = row.Value.Id;
                tReply.Add(Time(() => h.Run(reply, Tick(), rid, "answer", "TOKEN", token)));
                tResult.Add(Time(() => h.Run(result, Tick(), rid)));
                tAck.Add(Time(() => h.Run(ack, Tick(), token)));
                inflight.Dequeue();
                done++;

                // The idle poll: a worker asking again when its own call slots are full is what production does.
                tIdle.Add(Time(() => h.Run(claim, Tick(), "SVC", "idle-probe", "worker-1")));

                if (cp < Checkpoints.Length && done == Math.Min(Checkpoints[cp], calls))
                {
                    Line($"{done}\t{Avg(tClaim, windowStart, done):F3}\t{Avg(tIdle, windowStart, done):F3}");
                    windowStart = done; cp++;
                    if (Checkpoints[cp - 1] >= calls) break;
                }
            }

            Line("");
            Line("verb\tp50 ms\tp99 ms");
            Line(Pct("HW.CALL", tCall)); Line(Pct("HW.CLAIM SVC", tClaim)); Line(Pct("HW.CLAIM idle", tIdle));
            Line(Pct("HW.REPLY", tReply)); Line(Pct("HW.RESULT", tResult)); Line(Pct("HW.ACK (rpc)", tAck));

            // Queues and pub/sub, 10 000 each.
            const int Messages = 10_000;
            var tSend = new List<double>(Messages); var tQClaim = new List<double>(Messages); var tQAck = new List<double>(Messages);
            for (var i = 0; i < Messages; i++)
            {
                var id = $"m-{i}";
                tSend.Add(Time(() => h.Run(send, Tick(), "orders", id, "{}")));
                CommandHarness.Claim? row = null;
                tQClaim.Add(Time(() => row = h.ClaimOne("Q", "orders", "worker-1", Tick())));
                var token = row!.Value.Token;
                tQAck.Add(Time(() => h.Run(ack, Tick(), token)));
            }
            Line(Pct("HW.SEND", tSend)); Line(Pct("HW.CLAIM Q", tQClaim)); Line(Pct("HW.ACK (queue)", tQAck));

            h.Run(new HwSessionCommand(), Tick(), "worker-1", """{"subscriptions":[{"channel":"news","group":"billing"}]}""");
            var tPub = new List<double>(Messages); var tGClaim = new List<double>(Messages); var tGAck = new List<double>(Messages);
            for (var i = 0; i < Messages; i++)
            {
                var pid = $"p-{i}";
                tPub.Add(Time(() => h.Run(publish, Tick(), "news", "{}", "ID", pid)));
                CommandHarness.Claim? row = null;
                tGClaim.Add(Time(() => row = h.ClaimOne("Q", "news@billing", "worker-1", Tick())));
                var token = row!.Value.Token;
                tGAck.Add(Time(() => h.Run(ack, Tick(), token)));
            }
            Line(Pct("HW.PUBLISH (1 group)", tPub)); Line(Pct("HW.CLAIM Q (group)", tGClaim)); Line(Pct("HW.ACK (group)", tGAck));
        }
        finally
        {
            TempDirs.TryDelete(dir);
            var path = Environment.GetEnvironmentVariable("HW_PROBE_OUT");
            if (!string.IsNullOrEmpty(path)) File.WriteAllText(path, report.ToString());
        }
    }

    private static double Avg(List<double> xs, int from, int to)
    {
        double sum = 0;
        for (var i = from; i < to; i++) sum += xs[i];
        return to > from ? sum / (to - from) : 0;
    }

    private static string Pct(string name, List<double> xs)
    {
        var s = xs.ToArray(); Array.Sort(s);
        return $"{name}\t{s[(int)(s.Length * 0.50)]:F3}\t{s[Math.Min(s.Length - 1, (int)(s.Length * 0.99))]:F3}";
    }
}
