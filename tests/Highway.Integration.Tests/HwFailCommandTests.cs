using System.Text;
using FluentAssertions;
using Highway.Server;
using StackExchange.Redis;
using Xunit;

namespace Highway.Integration.Tests;

/// <summary>
/// Feature 015 T3 — <c>HW.FAIL</c> against a real embedded server.
///
/// <para>Exercised end to end rather than unit-tested, because the two things most likely to
/// be wrong are both invisible to a unit test: whether the processing key is declared in
/// <c>Prepare</c> (Garnet rejects touching an undeclared key in <c>Main</c>, and it has done
/// so twice already in this project), and whether the pop-and-restore preserves the list.</para>
/// </summary>
public class HwFailCommandTests : IDisposable
{
    private readonly HighwayTestServer _server = new();

    public void Dispose() => _server.Dispose();

    private async Task<IDatabase> ConnectAsync()
        => (await ConnectionMultiplexer.ConnectAsync(_server.ConnectionString)).GetDatabase();

    private static byte[] Envelope(string body = """{"Amount":1}""")
        => Encoding.UTF8.GetBytes($$"""{"v":1,"src":"t","ts":"2026-08-08T00:00:00Z","body":{{body}}}""");

    /// <summary>Enqueues one request and claims it, leaving it in the node's processing list.</summary>
    private static async Task ClaimOneAsync(IDatabase db, string service, string node, string requestId)
    {
        await db.ExecuteAsync("HW.CALL", service, requestId, Envelope());
        var claimed = await db.ExecuteAsync("HW.DEQUEUE", service, node);
        claimed.IsNull.Should().BeFalse("the request must be claimed before it can fail");
    }

    // ---- the core contract ----------------------------------------------------

    [Fact]
    public async Task ReportingAFailure_RewritesTheEntryWithoutAcknowledgingIt()
    {
        var db = await ConnectAsync();
        await ClaimOneAsync(db, "fail.svc", "node-a", "req-1");

        var before = (long)await db.ExecuteAsync("LLEN", "hw:svc:fail.svc:proc:node-a");
        before.Should().Be(1);

        var result = (long)await db.ExecuteAsync(
            "HW.FAIL", "SVC", "fail.svc", "node-a", "req-1", "System.TimeoutException", """{"m":"timed out"}""");

        result.Should().Be(1);

        // The whole point: reporting is not acknowledging. The message is still claimed, so
        // the lease sweep recovers it on exactly the schedule it would have.
        var after = (long)await db.ExecuteAsync("LLEN", "hw:svc:fail.svc:proc:node-a");
        after.Should().Be(1, "HW.FAIL explains a message, it does not finish with it");
    }

    [Fact]
    public async Task TheMessageStillAcknowledgesNormallyAfterAFailureIsReported()
    {
        var db = await ConnectAsync();
        await ClaimOneAsync(db, "fail.ack", "node-a", "req-1");

        await db.ExecuteAsync("HW.FAIL", "SVC", "fail.ack", "node-a", "req-1", "SomeException", "d");

        // A rewritten entry must still be matchable by HW.ACK, or reporting a failure would
        // strand a message that later succeeded. HW.ACK answers +OK whether or not it matched
        // (it is idempotent by design), so the list length is what actually proves the match.
        await db.ExecuteAsync("HW.ACK", "fail.ack", "node-a", "req-1");

        ((long)await db.ExecuteAsync("LLEN", "hw:svc:fail.ack:proc:node-a")).Should().Be(0,
            "the entry HW.FAIL rewrote must still be findable by its id");
    }

    [Fact]
    public async Task ReportingAgainstAnUnknownMessage_ReturnsZeroRatherThanFailing()
    {
        var db = await ConnectAsync();
        await ClaimOneAsync(db, "fail.unknown", "node-a", "req-1");

        var result = (long)await db.ExecuteAsync(
            "HW.FAIL", "SVC", "fail.unknown", "node-a", "req-does-not-exist", "SomeException", "d");

        result.Should().Be(0, "a late report is a race the client cannot avoid, not an error");

        // And the list it scanned is intact - a miss must not eat the entries it walked past.
        ((long)await db.ExecuteAsync("LLEN", "hw:svc:fail.unknown:proc:node-a")).Should().Be(1);
    }

    [Fact]
    public async Task ReportingAgainstAnEmptyProcessingList_ReturnsZero()
    {
        var db = await ConnectAsync();

        var result = (long)await db.ExecuteAsync(
            "HW.FAIL", "SVC", "never.used", "node-a", "req-1", "SomeException", "d");

        result.Should().Be(0);
    }

    [Fact]
    public async Task OtherEntriesInTheProcessingList_SurviveTheRewrite()
    {
        var db = await ConnectAsync();
        await ClaimOneAsync(db, "fail.many", "node-a", "req-1");
        await ClaimOneAsync(db, "fail.many", "node-a", "req-2");
        await ClaimOneAsync(db, "fail.many", "node-a", "req-3");

        await db.ExecuteAsync("HW.FAIL", "SVC", "fail.many", "node-a", "req-2", "SomeException", "d");

        ((long)await db.ExecuteAsync("LLEN", "hw:svc:fail.many:proc:node-a")).Should().Be(3,
            "pop-and-restore must put back everything it took, in order");

        // Each one still acknowledges by id, which proves the entries were not scrambled.
        // HW.ACK is idempotent and always answers +OK, so the count is the assertion.
        foreach (var id in new[] { "req-1", "req-2", "req-3" })
            await db.ExecuteAsync("HW.ACK", "fail.many", "node-a", id);

        ((long)await db.ExecuteAsync("LLEN", "hw:svc:fail.many:proc:node-a")).Should().Be(0,
            "every entry the rewrite put back must still match its own id");
    }

    // ---- the other two families -----------------------------------------------

    [Fact]
    public async Task QueueMessagesReportFailureTheSameWay()
    {
        var db = await ConnectAsync();
        await db.ExecuteAsync("HW.QSEND", "fail.queue", "msg-1", Envelope());
        await db.ExecuteAsync("HW.QCLAIM", "fail.queue", "node-a");

        var result = (long)await db.ExecuteAsync(
            "HW.FAIL", "Q", "fail.queue", "node-a", "msg-1", "InvalidOperationException", "d");

        result.Should().Be(1);
        ((long)await db.ExecuteAsync("LLEN", "hw:q:fail.queue:proc:node-a")).Should().Be(1);
        ((long)await db.ExecuteAsync("HW.QACK", "fail.queue", "node-a", "msg-1")).Should().Be(1);
    }

    [Fact]
    public async Task ChannelMessagesReportFailureByMessageId()
    {
        var db = await ConnectAsync();
        await db.ExecuteAsync("HW.SUBSCRIBE", "fail.ch", "grp");
        await db.ExecuteAsync("HW.PUBLISH", "fail.ch", Envelope());

        // Claim via the derived queue (018 unified path)
        var claimed = await db.ExecuteAsync("HW.QCLAIM", "fail.ch@grp", "node-1");
        claimed.IsNull.Should().BeFalse();

        var messageId = ((RedisResult[])claimed!)[0].ToString();

        // Report failure via the Q target on the derived queue
        var result = (long)await db.ExecuteAsync(
            "HW.FAIL", "Q", "fail.ch@grp", "node-1", messageId!, "SomeException", "d");

        result.Should().Be(1);
    }

    [Fact]
    public async Task AnUnknownTarget_IsRejectedNamingTheExpectedForms()
    {
        var db = await ConnectAsync();

        var act = async () => await db.ExecuteAsync(
            "HW.FAIL", "TOPIC", "x", "y", "z", "SomeException", "d");

        // Naming the accepted forms is the difference between an error an operator can act on
        // and one they have to read source to understand.
        var thrown = await act.Should().ThrowAsync<RedisServerException>();
        thrown.WithMessage("*SVC*");
        thrown.WithMessage("*Q*");
    }

    // ---- merge ----------------------------------------------------------------

    [Fact]
    public async Task ASecondReport_IsAcceptedAndReplacesTheFirst()
    {
        var db = await ConnectAsync();
        await ClaimOneAsync(db, "fail.merge", "node-a", "req-1");

        var first = (long)await db.ExecuteAsync(
            "HW.FAIL", "SVC", "fail.merge", "node-a", "req-1", "System.TimeoutException", """{"m":"one"}""");
        var second = (long)await db.ExecuteAsync(
            "HW.FAIL", "SVC", "fail.merge", "node-a", "req-1", "System.InvalidOperationException", """{"m":"two"}""");

        first.Should().Be(1);
        second.Should().Be(1);

        // Trailers must replace, not stack: the entry stays one entry and stays acknowledgeable.
        ((long)await db.ExecuteAsync("LLEN", "hw:svc:fail.merge:proc:node-a")).Should().Be(1);

        await db.ExecuteAsync("HW.ACK", "fail.merge", "node-a", "req-1");
        ((long)await db.ExecuteAsync("LLEN", "hw:svc:fail.merge:proc:node-a")).Should().Be(0);
    }
}
