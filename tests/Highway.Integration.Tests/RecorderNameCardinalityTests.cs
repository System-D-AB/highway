using FluentAssertions;
using Highway.Server;
using StackExchange.Redis;
using Xunit;

namespace Highway.Integration.Tests;

/// <summary>
/// Regression guard for a flight-recorder defect found by running the samples: an
/// <b>unbounded number of recorder buckets</b>.
///
/// <para><c>HW.REPLY</c> recorded its <c>RpcReplied</c> event under the <i>request
/// ID</i> as the name. The recorder buckets by name in a dictionary whose entries are
/// never removed — <c>Sweep()</c> reclaims events inside a buffer but never the buffer
/// itself — so every RPC permanently added a dictionary entry and a
/// <c>NameBuffer</c>. A busy broker grew without limit.</para>
///
/// <para>It also split the RPC lifecycle: a service's history showed
/// <c>RpcEnqueued</c> and <c>RpcClaimed</c>, while the reply sat in a bucket of its
/// own that nothing would think to query.</para>
///
/// <para><c>docs/HIGHWAY-PROTOCOL.md</c> already said recording happens "per name — a
/// service name or a channel name", so the document was right and the code was wrong.
/// These tests assert the documented contract.</para>
/// </summary>
public class RecorderNameCardinalityTests : IDisposable
{
    private readonly HighwayTestServer _server = new();

    public void Dispose() => _server.Dispose();

    private async Task<IDatabase> ConnectAsync()
    {
        var mux = await ConnectionMultiplexer.ConnectAsync(_server.ConnectionString);
        return mux.GetDatabase();
    }

    /// <summary>
    /// The bug, stated directly: distinct request IDs must not create distinct
    /// recorder buckets.
    /// </summary>
    [Fact]
    public async Task Replies_DoNotCreateABucketPerRequestId()
    {
        var db = await ConnectAsync();

        var requestIds = Enumerable.Range(0, 12)
            .Select(_ => Guid.NewGuid().ToString("N"))
            .ToArray();

        foreach (var id in requestIds)
            await db.ExecuteAsync("HW.REPLY", id, "payload"u8.ToArray());

        // No bucket may be named after a request ID. Before the fix each of these
        // returned exactly one event.
        foreach (var id in requestIds)
        {
            var strays = (RedisResult[])(await db.ExecuteAsync("HW.REPLAY", id))!;
            strays.Should().BeEmpty(
                "a request ID is not a service or channel name and must never become a recorder bucket");
        }
    }

    /// <summary>
    /// Fixing the leak must not lose the events — they move, they do not disappear.
    /// </summary>
    [Fact]
    public async Task Replies_AreAllRecordedUnderTheReservedName()
    {
        var db = await ConnectAsync();

        for (var i = 0; i < 5; i++)
            await db.ExecuteAsync("HW.REPLY", Guid.NewGuid().ToString("N"), "payload"u8.ToArray());

        var events = (RedisResult[])(await db.ExecuteAsync("HW.REPLAY", "hw.replies"))!;

        events.Should().HaveCount(5, "every reply is still recorded, just in one bucket");
    }

    /// <summary>
    /// The recorder's name count must stay flat as replies accumulate. This is the
    /// assertion that would catch the same mistake made in some future command,
    /// rather than only the one instance fixed here.
    /// </summary>
    [Fact]
    public async Task RecorderNameCount_DoesNotGrowWithTraffic()
    {
        var db = await ConnectAsync();

        for (var i = 0; i < 5; i++)
            await db.ExecuteAsync("HW.REPLY", Guid.NewGuid().ToString("N"), "p"u8.ToArray());

        var afterFive = await RecorderNameCountAsync(db);

        for (var i = 0; i < 25; i++)
            await db.ExecuteAsync("HW.REPLY", Guid.NewGuid().ToString("N"), "p"u8.ToArray());

        var afterThirty = await RecorderNameCountAsync(db);

        afterThirty.Should().Be(afterFive,
            "five times the traffic must not mean five times the buckets — that is the leak");
    }

    /// <summary>Reads the <c>names</c> field from the flat HW.STATS RECORDER reply.</summary>
    private static async Task<long> RecorderNameCountAsync(IDatabase db)
    {
        var fields = (RedisResult[])(await db.ExecuteAsync("HW.STATS", "RECORDER"))!;

        for (var i = 0; i + 1 < fields.Length; i += 2)
            if (string.Equals(fields[i].ToString(), "names", StringComparison.Ordinal))
                return (long)fields[i + 1];

        throw new InvalidOperationException("HW.STATS RECORDER did not report a 'names' field.");
    }
}
