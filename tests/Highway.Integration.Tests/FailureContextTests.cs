using System.Text;
using FluentAssertions;
using Highway.Server;
using StackExchange.Redis;
using Xunit;

namespace Highway.Integration.Tests;

/// <summary>
/// Feature 015 T5 and T13 — the reported failure has to survive all the way to the dead letter.
///
/// <para>Three hops, each of which could drop it silently: <c>HW.FAIL</c> writes the block into
/// the processing entry, the lease sweep re-encodes that entry as a <i>queue</i> entry on
/// requeue, and finally wraps it as a dead letter. The middle hop is the one worth testing —
/// a one-worker test would pass even if the context were cached client-side, which would defeat
/// the entire reason the state is held on the server.</para>
/// </summary>
public class FailureContextTests : IDisposable
{
    private const string Service = "fc.svc";

    private readonly HighwayTestServer _server = new(o =>
    {
        o.Lease = TimeSpan.FromMilliseconds(50);
        o.MaxDeliveryAttempts = 2;
    });

    public void Dispose() => _server.Dispose();

    private async Task<IDatabase> ConnectAsync()
        => (await ConnectionMultiplexer.ConnectAsync(_server.ConnectionString)).GetDatabase();

    /// <summary>Reads one dead letter as a field/value map — the shape HW.DLQ PEEK returns.</summary>
    private static async Task<Dictionary<string, string>> PeekOneAsync(IDatabase db, string service)
    {
        var result = (RedisResult[])(await db.ExecuteAsync("HW.DLQ", "PEEK", "SVC", service))!;
        result.Should().HaveCount(1, "exactly one message should have dead-lettered");

        var flat = (RedisResult[])result[0]!;
        var map = new Dictionary<string, string>();
        for (var i = 0; i + 1 < flat.Length; i += 2)
            map[flat[i].ToString()!] = flat[i + 1].ToString()!;
        return map;
    }

    /// <summary>
    /// <b>The test this feature exists for.</b> Two different workers fail the same message in
    /// two different ways; the dead letter has to report both the last failure and where it
    /// started.
    /// </summary>
    [Fact]
    public async Task FirstType_SurvivesRequeue_AcrossTwoWorkers()
    {
        var db = await ConnectAsync();
        await db.ExecuteAsync("HW.CALL", Service, "req-1",
            Encoding.UTF8.GetBytes("""{"v":1,"src":"t","ts":"2026-08-08T00:00:00Z","body":{}}"""));

        // node-a claims it and reports a timeout, then dies without acknowledging.
        await db.ExecuteAsync("HW.DEQUEUE", Service, "node-a");
        await db.ExecuteAsync("HW.FAIL", "SVC", Service, "node-a", "req-1",
            "System.TimeoutException", """{"message":"the call timed out"}""");
        await Task.Delay(70);   // lease expires

        // node-b picks up the requeued message. If the sweep dropped the block here, the
        // failure history is already gone and nothing says so.
        await db.ExecuteAsync("HW.DEQUEUE", Service, "node-b");
        await db.ExecuteAsync("HW.FAIL", "SVC", Service, "node-b", "req-1",
            "System.InvalidOperationException", """{"message":"invalid state"}""");
        await Task.Delay(70);

        // Exhaust the remaining attempts so it dead-letters.
        for (var i = 0; i < 4; i++)
        {
            await db.ExecuteAsync("HW.DEQUEUE", Service, "node-b");
            await Task.Delay(70);
        }
        await db.ExecuteAsync("HW.DEQUEUE", Service, "node-b");

        var dead = await PeekOneAsync(db, Service);

        dead.Should().ContainKey("failureType").WhoseValue.Should().Be("System.InvalidOperationException",
            "the LAST failure is what the message died of");
        dead.Should().ContainKey("failureFirstType").WhoseValue.Should().Be("System.TimeoutException",
            "firstType is the whole point: it says the failure CHANGED, which is the question " +
            "an operator actually asks");
        dead["failureDetail"].Should().Contain("invalid state");
        dead.Should().NotContainKey("failure", "a dead letter with context must not also claim it has none");
    }

    [Fact]
    public async Task AFailureReportedOnce_SurvivesToTheDeadLetterWithNoFirstType()
    {
        var db = await ConnectAsync();
        await db.ExecuteAsync("HW.CALL", Service, "req-2",
            Encoding.UTF8.GetBytes("""{"v":1,"src":"t","ts":"2026-08-08T00:00:00Z","body":{}}"""));

        await db.ExecuteAsync("HW.DEQUEUE", Service, "node-a");
        await db.ExecuteAsync("HW.FAIL", "SVC", Service, "node-a", "req-2",
            "System.TimeoutException", """{"message":"one"}""");

        for (var i = 0; i < 6; i++)
        {
            await Task.Delay(70);
            await db.ExecuteAsync("HW.DEQUEUE", Service, "node-a");
        }

        var dead = await PeekOneAsync(db, Service);

        dead["failureType"].Should().Be("System.TimeoutException");
        dead.Should().NotContainKey("failureFirstType",
            "it failed the same way every time, and a firstType equal to type would be noise");
    }

    /// <summary>
    /// R3.7 — a worker that crashed before it could report leaves no context. That must be
    /// stated, not shown as blanks: "nothing was reported" and "something was reported and it
    /// was empty" are different situations for whoever is holding the pager.
    /// </summary>
    [Fact]
    public async Task ADeadLetterWithNoReportedFailure_SaysSoExplicitly()
    {
        var db = await ConnectAsync();
        await db.ExecuteAsync("HW.CALL", Service, "req-3",
            Encoding.UTF8.GetBytes("""{"v":1,"src":"t","ts":"2026-08-08T00:00:00Z","body":{}}"""));

        for (var i = 0; i < 6; i++)
        {
            await db.ExecuteAsync("HW.DEQUEUE", Service, "node-a");
            await Task.Delay(70);
        }
        await db.ExecuteAsync("HW.DEQUEUE", Service, "node-a");

        var dead = await PeekOneAsync(db, Service);

        dead.Should().NotContainKey("failureType");
        dead.Should().ContainKey("failure");
        dead["failure"].Should().Contain("not reported");
    }

    /// <summary>
    /// The payload must be unaffected by all of this. The block is a trailer, and a trailer
    /// that leaks into the payload would corrupt every replayed message.
    /// </summary>
    [Fact]
    public async Task TheOriginalPayload_IsUnchangedByTheFailureBlock()
    {
        var db = await ConnectAsync();
        const string body = """{"v":1,"src":"t","ts":"2026-08-08T00:00:00Z","body":{"Amount":42}}""";
        await db.ExecuteAsync("HW.CALL", Service, "req-4", Encoding.UTF8.GetBytes(body));

        await db.ExecuteAsync("HW.DEQUEUE", Service, "node-a");
        await db.ExecuteAsync("HW.FAIL", "SVC", Service, "node-a", "req-4", "SomeException", "detail");

        for (var i = 0; i < 6; i++)
        {
            await Task.Delay(70);
            await db.ExecuteAsync("HW.DEQUEUE", Service, "node-a");
        }

        var dead = await PeekOneAsync(db, Service);

        dead["payload"].Should().Be(body, "the trailer must never bleed into the payload");
        dead["requestId"].Should().Be("req-4");
    }
}
