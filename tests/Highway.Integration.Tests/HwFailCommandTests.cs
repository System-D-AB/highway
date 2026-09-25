using System.Text;
using FluentAssertions;
using Highway.Server;
using StackExchange.Redis;
using Xunit;

namespace Highway.Integration.Tests;

/// <summary>
/// Feature 015 T3 — <c>HW.FAIL</c> against a real embedded server.
///
/// <para>Exercised end to end rather than unit-tested, because the thing most likely to be
/// wrong is invisible to a unit test: whether rewriting the claimed entry's failure block
/// leaves the claim itself intact — still in flight, still settled by the same token, and
/// its neighbours untouched.</para>
///
/// <para>Protocol 5.0: HW.FAIL names the claim by the token HW.CLAIM returned
/// (<c>HW.FAIL token type detail</c>, was <c>HW.FAIL SVC|Q name node id type detail</c>), and
/// answers <c>:1</c> recorded / <c>:0</c> not held.</para>
/// </summary>
public class HwFailCommandTests : IDisposable
{
    private readonly HighwayTestServer _server = new();

    public void Dispose() => _server.Dispose();

    private async Task<IDatabase> ConnectAsync()
        => (await ConnectionMultiplexer.ConnectAsync(_server.ConnectionString)).GetDatabase();

    private static byte[] Envelope(string body = """{"Amount":1}""")
        => Encoding.UTF8.GetBytes($$"""{"v":1,"src":"t","ts":"2026-08-08T00:00:00Z","body":{{body}}}""");

    /// <summary>Enqueues one request and claims it, leaving it in flight for the node; returns its token.</summary>
    private static async Task<string> ClaimOneAsync(IDatabase db, string service, string node, string requestId)
    {
        await db.ExecuteAsync("HW.CALL", service, requestId, Envelope());
        var claimed = await db.ClaimSvcAsync(service, node);
        claimed.Should().NotBeNull("the request must be claimed before it can fail");
        claimed!.Value.Id.Should().Be(requestId);
        return claimed.Value.Token;
    }

    private static async Task<long> FailAsync(IDatabase db, string token, string type, string detail)
        => (long)await db.ExecuteAsync("HW.FAIL", token, type, detail);

    // ---- the core contract ----------------------------------------------------

    [Fact]
    public async Task ReportingAFailure_RewritesTheEntryWithoutAcknowledgingIt()
    {
        var db = await ConnectAsync();
        var token = await ClaimOneAsync(db, "fail.svc", "node-a", "req-1");

        var before = _server.Inspect.ListLength("hw:svc:fail.svc:proc:node-a");
        before.Should().Be(1);

        var result = await FailAsync(db, token, "System.TimeoutException", """{"m":"timed out"}""");

        result.Should().Be(1);

        // The whole point: reporting is not acknowledging. The message is still claimed, so
        // the lease sweep recovers it on exactly the schedule it would have.
        var after = _server.Inspect.ListLength("hw:svc:fail.svc:proc:node-a");
        after.Should().Be(1, "HW.FAIL explains a message, it does not finish with it");
    }

    [Fact]
    public async Task TheMessageStillAcknowledgesNormallyAfterAFailureIsReported()
    {
        var db = await ConnectAsync();
        var token = await ClaimOneAsync(db, "fail.ack", "node-a", "req-1");

        await FailAsync(db, token, "SomeException", "d");

        // A rewritten entry must still be settled by its token, or reporting a failure would
        // strand a message that later succeeded.
        (await db.AckAsync(token)).Should().Be(1, "the claim HW.FAIL rewrote must still be held under its token");

        (_server.Inspect.ListLength("hw:svc:fail.ack:proc:node-a")).Should().Be(0,
            "the entry HW.FAIL rewrote must still be findable by its token");
    }

    [Fact]
    public async Task ReportingAgainstAnUnknownMessage_ReturnsZeroRatherThanFailing()
    {
        var db = await ConnectAsync();
        await ClaimOneAsync(db, "fail.unknown", "node-a", "req-1");
        var settled = await ClaimOneAsync(db, "fail.unknown", "node-a", "req-2");
        (await db.AckAsync(settled)).Should().Be(1);

        // A report that arrives after the message was settled names a claim no longer held.
        var result = await FailAsync(db, settled, "SomeException", "d");

        result.Should().Be(0, "a late report is a race the client cannot avoid, not an error");

        // And the node's other in-flight work is intact - a miss must not touch it.
        (_server.Inspect.ListLength("hw:svc:fail.unknown:proc:node-a")).Should().Be(1);
    }

    [Fact]
    public async Task ReportingAgainstAnEmptyProcessingList_ReturnsZero()
    {
        var db = await ConnectAsync();

        // Well-formed, but nothing was ever claimed from any box it could name.
        var result = await FailAsync(db, "999999.1", "SomeException", "d");

        result.Should().Be(0);
    }

    [Fact]
    public async Task OtherEntriesInTheProcessingList_SurviveTheRewrite()
    {
        var db = await ConnectAsync();
        var t1 = await ClaimOneAsync(db, "fail.many", "node-a", "req-1");
        var t2 = await ClaimOneAsync(db, "fail.many", "node-a", "req-2");
        var t3 = await ClaimOneAsync(db, "fail.many", "node-a", "req-3");

        await FailAsync(db, t2, "SomeException", "d");

        (_server.Inspect.ListLength("hw:svc:fail.many:proc:node-a")).Should().Be(3,
            "the rewrite must leave every in-flight entry in place");

        // Each one still acknowledges by its own token, which proves the entries were not scrambled.
        foreach (var token in new[] { t1, t2, t3 })
            (await db.AckAsync(token)).Should().Be(1);

        (_server.Inspect.ListLength("hw:svc:fail.many:proc:node-a")).Should().Be(0,
            "every entry the rewrite left must still match its own token");
    }

    // ---- the other two families -----------------------------------------------

    [Fact]
    public async Task QueueMessagesReportFailureTheSameWay()
    {
        var db = await ConnectAsync();
        await db.SendAsync("fail.queue", "msg-1", Envelope());
        var claimed = await db.ClaimQAsync("fail.queue", "node-a");
        claimed.Should().NotBeNull();

        var result = await FailAsync(db, claimed!.Value.Token, "InvalidOperationException", "d");

        result.Should().Be(1);
        (_server.Inspect.ListLength("hw:q:fail.queue:proc:node-a")).Should().Be(1);
        (await db.AckAsync(claimed.Value.Token)).Should().Be(1);
    }

    [Fact]
    public async Task ChannelMessagesReportFailureByMessageId()
    {
        var db = await ConnectAsync();
        await db.SubscribeAsync("fail.ch", "grp");
        await db.ExecuteAsync("HW.PUBLISH", "fail.ch", Envelope());

        // Claim via the derived queue (018 unified path)
        var claimed = await db.ClaimQAsync("fail.ch@grp", "node-1");
        claimed.Should().NotBeNull();

        // Report failure by the claim's token on the derived queue
        var result = await FailAsync(db, claimed!.Value.Token, "SomeException", "d");

        result.Should().Be(1);
    }

    [Fact]
    public async Task AnUnknownTarget_IsRejectedNamingTheExpectedForms()
    {
        var db = await ConnectAsync();

        // 5.0: the SVC|Q target moved from HW.FAIL to HW.CLAIM (HW.FAIL takes only the token).
        var act = async () => await db.ExecuteAsync("HW.CLAIM", "TOPIC", "x", "y");

        // Naming the accepted forms is the difference between an error an operator can act on
        // and one they have to read source to understand.
        var thrown = await act.Should().ThrowAsync<RedisServerException>();
        thrown.WithMessage("*SVC*");
        thrown.WithMessage("*Q*");

        // And a token HW.FAIL cannot parse says where a token comes from.
        var badToken = async () => await db.ExecuteAsync("HW.FAIL", "not-a-token", "SomeException", "d");
        (await badToken.Should().ThrowAsync<RedisServerException>())
            .WithMessage("ERR HW_INVALID_TOKEN*HW.CLAIM*");
    }

    // ---- merge ----------------------------------------------------------------

    [Fact]
    public async Task ASecondReport_IsAcceptedAndReplacesTheFirst()
    {
        var db = await ConnectAsync();
        var token = await ClaimOneAsync(db, "fail.merge", "node-a", "req-1");

        var first = await FailAsync(db, token, "System.TimeoutException", """{"m":"one"}""");
        var second = await FailAsync(db, token, "System.InvalidOperationException", """{"m":"two"}""");

        first.Should().Be(1);
        second.Should().Be(1);

        // Trailers must replace, not stack: the entry stays one entry and stays acknowledgeable.
        (_server.Inspect.ListLength("hw:svc:fail.merge:proc:node-a")).Should().Be(1);

        (await db.AckAsync(token)).Should().Be(1);
        (_server.Inspect.ListLength("hw:svc:fail.merge:proc:node-a")).Should().Be(0);
    }
}
