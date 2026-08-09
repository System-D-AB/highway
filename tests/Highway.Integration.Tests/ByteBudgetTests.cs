using System.Text;
using FluentAssertions;
using Highway.Server;
using StackExchange.Redis;
using Xunit;

namespace Highway.Integration.Tests;

/// <summary>
/// Feature 016 Phase 3 — byte budgets, refusal, and the drift check.
///
/// <para><b>The budget is per queue, not per server</b> (decision 1). Ten queues at their limit
/// is ten gigabytes; this does not bound the process, and constraint C4.7 records that gap
/// rather than leaving it to be inferred from the name.</para>
///
/// <para>After 018 one limit covers both verbs: a <c>SendAsync</c> queue and the per-group queue
/// a <c>PublishAsync</c> fans out into are the same structure.</para>
/// </summary>
public class ByteBudgetTests : IDisposable
{
    // Small enough to cross in a handful of messages, large enough for several to fit.
    private const long Limit = 16 * 1024;

    private readonly HighwayTestServer _server = new(o => o.MaxQueueBytes = Limit);

    public void Dispose() => _server.Dispose();

    private IDatabase Db() =>
        ConnectionMultiplexer.Connect(_server.ConnectionString).GetDatabase();

    private static byte[] Payload(int bytes = 2048)
        => Encoding.UTF8.GetBytes(
            "{\"v\":1,\"src\":\"t\",\"ts\":\"2026-08-09T00:00:00Z\",\"body\":{\"Blob\":\""
            + new string('x', bytes) + "\"}}");

    private static long Counter(IDatabase db, string queue)
    {
        var raw = db.Execute("GET", $"hw:q:{queue}:bytes");
        return raw.IsNull ? 0 : (long)raw;
    }

    // ---- the counter ----------------------------------------------------------

    [Fact]
    public void TheCounterTracksTheQueue()
    {
        var db = Db();

        Counter(db, "bb.count").Should().Be(0, "an untouched queue has no counter key at all");

        db.Execute("HW.QSEND", "bb.count", "m-1", Payload());
        var afterOne = Counter(db, "bb.count");
        afterOne.Should().BeGreaterThan(0);

        db.Execute("HW.QSEND", "bb.count", "m-2", Payload());
        Counter(db, "bb.count").Should().BeGreaterThan(afterOne, "a second message adds to it");

        // A claim moves the message out of the live queue, so the bytes go with it.
        db.Execute("HW.QCLAIM", "bb.count", "node-a");
        Counter(db, "bb.count").Should().Be(afterOne,
            "a claimed message has left the live queue - the budget governs what is waiting");
    }

    /// <summary>
    /// T8 — the counter is O(1) because it trusts every writer. This is what catches a writer
    /// that forgets, which is the only way a counter goes wrong and the paths nobody thought
    /// about are exactly the ones that do it.
    /// </summary>
    [Fact]
    public void TheCounterDoesNotDriftFromReality()
    {
        var db = Db();
        const string q = "bb.drift";

        // A mixed workload: sends, claims, acks, and an abandoned claim that the sweep requeues.
        for (var i = 0; i < 6; i++)
            db.Execute("HW.QSEND", q, $"m-{i}", Payload(512));

        db.Execute("HW.QCLAIM", q, "node-a");
        db.Execute("HW.QCLAIM", q, "node-a");
        db.Execute("HW.QACK", q, "node-a", "m-0");
        db.Execute("HW.QSEND", q, "m-late", Payload(512));

        // Recompute the truth from the structure itself and compare.
        var entries = (RedisResult[])db.Execute("LRANGE", $"hw:q:{q}:q", "0", "-1")!;
        var actual = entries.Sum(e => ((byte[])e!).Length);

        Counter(db, q).Should().Be(actual,
            "the counter is maintained by hand on every path, so it is only correct while every " +
            "path remembers - this test is what notices when one stops");
    }

    // ---- refusal --------------------------------------------------------------

    [Fact]
    public void AFullQueue_RefusesRatherThanDropping()
    {
        var db = Db();
        const string q = "bb.full";

        var accepted = 0;
        RedisServerException? refusal = null;

        for (var i = 0; i < 50; i++)
        {
            try { db.Execute("HW.QSEND", q, $"m-{i}", Payload()); accepted++; }
            catch (RedisServerException ex) { refusal = ex; break; }
        }

        refusal.Should().NotBeNull("the limit must eventually refuse");
        refusal!.Message.Should().Contain("HW_QUEUE_FULL");
        refusal.Message.Should().Contain(q, "the error names the queue that is full");
        refusal.Message.Should().Contain("not stored");

        // Nothing was discarded to make room: every accepted message is still there.
        ((long)db.Execute("LLEN", $"hw:q:{q}:q")).Should().Be(accepted,
            "refusing the producer is honest; dropping the oldest would lose exactly the " +
            "unprocessed work the queue exists to protect");
    }

    [Fact]
    public void HwQueueFull_IsPermanent_NotTransient()
    {
        var db = Db();
        const string q = "bb.class";

        RedisServerException? refusal = null;
        for (var i = 0; i < 50 && refusal is null; i++)
        {
            try { db.Execute("HW.QSEND", q, $"m-{i}", Payload()); }
            catch (RedisServerException ex) { refusal = ex; }
        }

        // The ERR HW_ prefix is the 004.1 marker for permanent: the client does not retry it.
        // Auto-retrying into a full queue holds a connection and hammers a broker already over
        // budget; backpressure is information the application has to act on.
        refusal!.Message.Should().StartWith("ERR HW_QUEUE_FULL");
    }

    [Fact]
    public void DrainingTheQueue_RestoresHeadroom()
    {
        var db = Db();
        const string q = "bb.drain";

        var accepted = 0;
        for (var i = 0; i < 50; i++)
        {
            try { db.Execute("HW.QSEND", q, $"m-{i}", Payload()); accepted++; }
            catch (RedisServerException) { break; }
        }

        for (var i = 0; i < accepted; i++)
            db.Execute("HW.QCLAIM", q, "node-a");

        // A limit that never recovers is a broken queue, not a bounded one.
        var act = () => db.Execute("HW.QSEND", q, "after-drain", Payload());
        act.Should().NotThrow("claiming the backlog frees the budget it was using");
    }

    // ---- fan-out (T10) --------------------------------------------------------

    /// <summary>
    /// The question 018 created: fan-out is one transaction, so a full group queue fails the
    /// publish for every group. Both halves are the requirement — nothing partially written,
    /// and the error names the group so an operator fixes a subscriber, not a channel.
    /// </summary>
    [Fact]
    public void AFullGroupQueue_RefusesTheWholePublish_AndNamesTheGroup()
    {
        var db = Db();
        const string channel = "bb.chan";

        // Fill billing by publishing while it is the ONLY subscriber. A group queue cannot be
        // filled directly — 018 reserves '@' in queue names precisely so a queue cannot
        // impersonate a group — so the legitimate path is the only path, which is the point.
        db.Execute("HW.SUBSCRIBE", channel, "billing");

        for (var i = 0; i < 50; i++)
        {
            try { db.Execute("HW.PUBLISH", channel, Payload()); }
            catch (RedisServerException) { break; }
        }

        // shipping joins afterwards, so its queue starts empty and healthy.
        db.Execute("HW.SUBSCRIBE", channel, "shipping");
        var shippingBefore = (long)db.Execute("LLEN", $"hw:q:{channel}@shipping:q");
        shippingBefore.Should().Be(0, "a group registered after the fact starts empty (C2.4)");

        var act = () => db.Execute("HW.PUBLISH", channel, Payload());
        var thrown = act.Should().Throw<RedisServerException>();

        thrown.WithMessage("*HW_QUEUE_FULL*");
        thrown.WithMessage("*billing*");   // which subscriber to go and fix

        ((long)db.Execute("LLEN", $"hw:q:{channel}@shipping:q")).Should().Be(shippingBefore,
            "a publish reaches every registered group or none - delivering to the groups that " +
            "fit would quietly downgrade C2.1 to 'at least once, unless full'");
    }

    [Fact]
    public void AHealthyChannel_PublishesNormally()
    {
        var db = Db();
        const string channel = "bb.ok";

        db.Execute("HW.SUBSCRIBE", channel, "billing");
        db.Execute("HW.SUBSCRIBE", channel, "shipping");

        db.Execute("HW.PUBLISH", channel, Payload(64));

        ((long)db.Execute("LLEN", $"hw:q:{channel}@billing:q")).Should().Be(1);
        ((long)db.Execute("LLEN", $"hw:q:{channel}@shipping:q")).Should().Be(1);
        Counter(db, $"{channel}@billing").Should().BeGreaterThan(0,
            "a group queue is a queue, so it is accounted for like one");
    }

    // ---- refusals are visible (016 R4.6) --------------------------------------

    [Fact]
    public void RefusalsAreCountedInStats()
    {
        var db = Db();
        const string q = "bb.counted";

        var refused = 0;
        for (var i = 0; i < 50; i++)
        {
            try { db.Execute("HW.QSEND", q, $"m-{i}", Payload()); }
            catch (RedisServerException) { refused++; }
        }

        refused.Should().BeGreaterThan(0);

        var stats = (RedisResult[])db.Execute("HW.STATS")!;
        var map = new Dictionary<string, string>();
        for (var i = 0; i + 1 < stats.Length; i += 2)
            map[stats[i].ToString()!] = stats[i + 1].ToString()!;

        map.Should().ContainKey("sendsRefused");
        int.Parse(map["sendsRefused"]).Should().Be(refused,
            "a producer sees its own refusal; an operator needs the rate, or a full queue gets " +
            "blamed on the network");
    }

    /// <summary>
    /// A refusal is decided in <c>Main</c>, which the command's <c>Failed</c> flag does not
    /// cover — so without an explicit return it fell through to the doorbell and recorded
    /// itself as <c>Published</c>. Waking every subscriber for a message that does not exist.
    /// </summary>
    [Fact]
    public void ARefusedPublish_IsNotRecordedAsPublished()
    {
        var db = Db();
        const string channel = "bb.norecord";

        db.Execute("HW.SUBSCRIBE", channel, "grp");
        for (var i = 0; i < 50; i++)
        {
            try { db.Execute("HW.PUBLISH", channel, Payload()); }
            catch (RedisServerException) { break; }
        }

        var replay = (RedisResult[])db.Execute("HW.REPLAY", channel)!;
        var flat = replay
            .SelectMany(e => ((RedisResult[])e!).Select(f => f.ToString() ?? ""))
            .ToArray();

        flat.Any(f => f.Contains("SendRefused")).Should().BeTrue("the refusal is recorded");

        var published = flat.Count(f => f == "Published");
        var queueLen = (long)db.Execute("LLEN", $"hw:q:{channel}@grp:q");
        published.Should().Be((int)queueLen,
            "exactly the publishes that were stored may be recorded as Published - a refused " +
            "one wrote nothing");
    }

    [Fact]
    public void ZeroDisablesTheLimit()
    {
        using var unlimited = new HighwayTestServer(o => o.MaxQueueBytes = 0);
        var db = ConnectionMultiplexer.Connect(unlimited.ConnectionString).GetDatabase();

        for (var i = 0; i < 40; i++)
            db.Execute("HW.QSEND", "bb.unlimited", $"m-{i}", Payload());

        ((long)db.Execute("LLEN", "hw:q:bb.unlimited:q")).Should().Be(40,
            "zero is documented as 'no limit', and an operator who sets it means it");
    }
}
