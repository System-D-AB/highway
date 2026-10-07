// 074 (D2): RPC has no dead letters — these dead-letter mechanics tests drive a queue (HW.SEND / HW.CLAIM Q),
// where dead letters exist. They were written against a service before 074.
using FluentAssertions;
using Highway.Server;
using StackExchange.Redis;
using Xunit;

namespace Highway.Integration.Tests;

/// <summary>
/// Feature 013 Part 1 — the defect this feature exists for.
///
/// <para>Before this change, the service claim's lease sweep (<c>HW.CLAIM SVC</c> in 5.0) requeued abandoned work with
/// no attempt limit and no destination. A request whose handler failed every time was
/// claimed, abandoned, requeued, claimed — for the life of the deployment. Because the
/// queue is FIFO, it was also retried <i>ahead of everything behind it</i>, so one poison
/// message did not merely waste work: it blocked the service.</para>
///
/// <para>These tests drive the sweep directly by claiming without acknowledging, which is
/// exactly what a crashed or hanging consumer does.</para>
/// </summary>
public class DeadLetterTests : IDisposable
{
    private const string Queue = "dlq.q";

    // A lease short enough that the sweep fires between calls, so a test does not
    // have to wait out the 5-minute production default.
    private readonly HighwayTestServer _server = new(o =>
    {
        o.Lease = TimeSpan.FromMilliseconds(50);
        o.MaxDeliveryAttempts = 3;
    });

    public void Dispose() => _server.Dispose();

    private async Task<IDatabase> ConnectAsync()
        => (await ConnectionMultiplexer.ConnectAsync(_server.ConnectionString)).GetDatabase();

    /// <summary>Claims the request and never acknowledges it — a crashed consumer.</summary>
    private static async Task AbandonOnceAsync(IDatabase db, string service, string node)
    {
        await db.ClaimQAsync(service, node);
        await Task.Delay(70);   // let the lease expire
    }

    private long ListLength(string key) => _server.Inspect.ListLength(key);

    /// <summary>
    /// <b>The regression this feature is for.</b> A request that is never acknowledged
    /// must stop being redelivered.
    /// </summary>
    [Fact]
    public async Task PoisonMessage_StopsBeingRedelivered_AndLandsInTheDlq()
    {
        var db = await ConnectAsync();
        await db.ExecuteAsync("HW.SEND", Queue, "poison-1", "payload"u8.ToArray());

        // MaxDeliveryAttempts = 3, so the fourth requeue attempt dead-letters it.
        for (var i = 0; i < 6; i++)
            await AbandonOnceAsync(db, Queue, "node-a");

        // One more claim to run the sweep that moves it.
        await db.ClaimQAsync(Queue, "node-a");

        var queue = ListLength($"hw:q:{Queue}:q");
        var dlq   = ListLength($"hw:q:{Queue}:dlq");

        dlq.Should().Be(1, "the request exhausted its attempts and must leave the live queue");
        queue.Should().Be(0, "a dead-lettered request must not still be queued for retry");
    }

    /// <summary>
    /// Requirement 2 AC3. The move is one Garnet transaction, so there is no window in
    /// which the entry is visible in both lists or in neither.
    /// </summary>
    [Fact]
    public async Task DeadLettering_IsAtomic_NeverInBothListsNorNeither()
    {
        var db = await ConnectAsync();
        await db.ExecuteAsync("HW.SEND", Queue, "atomic-1", "payload"u8.ToArray());

        for (var i = 0; i < 8; i++)
        {
            await AbandonOnceAsync(db, Queue, "node-a");

            var queue = ListLength($"hw:q:{Queue}:q");
            var proc  = ListLength($"hw:q:{Queue}:proc:node-a");
            var dlq   = ListLength($"hw:q:{Queue}:dlq");

            (queue + proc + dlq).Should().Be(1,
                "the request exists exactly once across the queue, the processing list and the DLQ");
        }
    }

    /// <summary>
    /// A message delivered once and acknowledged has one attempt, not two — so ordinary
    /// traffic must never approach the limit.
    /// </summary>
    [Fact]
    public async Task SuccessfulDelivery_NeverDeadLetters()
    {
        var db = await ConnectAsync();

        for (var i = 0; i < 10; i++)
        {
            var id = $"ok-{i}";
            await db.ExecuteAsync("HW.SEND", Queue, id, "payload"u8.ToArray());
            var claimed = await db.ClaimQAsync(Queue, "node-a");
            (await db.AckAsync(claimed!.Value.Token)).Should().Be(1);
        }

        (ListLength($"hw:q:{Queue}:dlq")).Should().Be(0);
    }

    /// <summary>
    /// Requirement 2 AC10 — the documented escape hatch restores the old behaviour for
    /// anyone who genuinely wants unbounded retries.
    /// </summary>
    [Fact]
    public async Task MaxDeliveryAttemptsZero_RestoresUnlimitedRetries()
    {
        using var unlimited = new HighwayTestServer(o =>
        {
                        o.Lease = TimeSpan.FromMilliseconds(50);
            o.MaxDeliveryAttempts = 0;
        });

        var db = (await ConnectionMultiplexer.ConnectAsync(unlimited.ConnectionString)).GetDatabase();
        await db.ExecuteAsync("HW.SEND", Queue, "forever-1", "payload"u8.ToArray());

        for (var i = 0; i < 8; i++)
            await AbandonOnceAsync(db, Queue, "node-a");

        (ListLength($"hw:q:{Queue}:dlq")).Should().Be(0,
            "0 means unlimited, which is how Highway behaved before this feature");
    }

    /// <summary>
    /// Requirement 2 AC9. An unattended dead-letter list must not exhaust the server it
    /// exists to protect.
    /// </summary>
    [Fact]
    public async Task DeadLetterList_IsBounded()
    {
        using var tiny = new HighwayTestServer(o =>
        {
                        o.Lease = TimeSpan.FromMilliseconds(50);
            o.MaxDeliveryAttempts = 1;
            o.MaxDeadLetterEntries = 3;
        });

        var db = (await ConnectionMultiplexer.ConnectAsync(tiny.ConnectionString)).GetDatabase();

        for (var i = 0; i < 8; i++)
        {
            await db.ExecuteAsync("HW.SEND", Queue, $"flood-{i}", "payload"u8.ToArray());
            await AbandonOnceAsync(db, Queue, "node-a");
            await AbandonOnceAsync(db, Queue, "node-a");
        }

        var dlq = ListLength($"hw:q:{Queue}:dlq");
        dlq.Should().BeLessThanOrEqualTo(3, "the dead-letter list is capped");
    }
}
