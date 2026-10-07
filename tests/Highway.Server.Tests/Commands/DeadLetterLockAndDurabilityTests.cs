using System.Text;
using FluentAssertions;
using Highway.Server.Commands.Ported;
using Highway.Server.Commands.Runtime;
using Highway.Server.Resp;
using Highway.Server.Storage;
using Highway.Server.Storage.Rocks;
using Highway.Server.Tests.Storage;
using Xunit;

namespace Highway.Server.Tests.Commands;

/// <summary>
/// 073 tests its own task list required and the first delivery did not have (found in verification): no command on
/// the dead-letter path waits on a lock it already holds; a dead letter and its notification are durable together;
/// a standby refuses the dead-letter verbs.
/// </summary>
public class DeadLetterLockAndDurabilityTests
{
    private static readonly long T0 = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc).Ticks;
    private static long At(double seconds) => T0 + TimeSpan.FromSeconds(seconds).Ticks;
    private static string Text(byte[] b) => CommandHarness.AsText(b);

    private static HighwayServerOptions Options() => new() { MaxDeliveryAttempts = 1, Lease = TimeSpan.FromSeconds(5) };

    /// <summary>
    /// Sends one message to <paramref name="queue"/> at <paramref name="at"/> and lets two leases lapse: a fresh
    /// message's first expiry is attempt 1, within <c>MaxDeliveryAttempts = 1</c>; the second dead-letters it.
    /// Dead at <c>at + 20 s</c>.
    /// </summary>
    private static void DeadLetterOne(CommandHarness h, string queue, string id, double at)
    {
        Text(h.Run(new HwSendCommand(), At(at), queue, id, "{}")).Should().Be("+OK\r\n");
        ExpireTwice(h, queue, at);
    }

    private static void ExpireTwice(CommandHarness h, string queue, double at)
    {
        CommandHarness.Claims(h.Run(new HwClaimCommand(), At(at), "Q", queue, "node-1")).Should().ContainSingle();
        CommandHarness.Claims(h.Run(new HwClaimCommand(), At(at + 10), "Q", queue, "node-2")).Should().ContainSingle("redelivered once");
        h.Run(new HwClaimCommand(), At(at + 20), "Q", queue, "node-3");
    }

    private static string ClaimDeadToken(CommandHarness h, double at, string queue)
        => CommandHarness.Claims(h.Run(new HwClaimCommand(), At(at), "DEAD", "Q", queue, "worker-1")).Single().Token;

    [Fact]
    public async Task WithEveryLockOnOneStripe_TheWholeDeadLetterFlow_NeverWaitsOnItself()
    {
        // One stripe: every pair of names collides. A path that took the target's lock and then, separately, the
        // feed's would block on itself here — a deadlock that 256 stripes hide until two names happen to collide.
        var run = Task.Run(() =>
        {
            using var h = new CommandHarness(options: Options(), locks: new StripedLock(1));
            Text(h.Run(new HwSessionCommand(), At(0), "worker-1",
                """{"subscriptions":[{"channel":"news","group":"billing"}],"deadLetters":[{"kind":"Q","name":"orders"},{"kind":"Q","name":"news@billing"}]}"""))
                .Should().Be("+OK\r\n");

            DeadLetterOne(h, "orders", "o-1", 0);                                        // dead at 20
            Text(h.Run(new HwDlqCommand(), At(21), "RESOLVE", ClaimDeadToken(h, 21, "orders"), "REQUEUE")).Should().Contain("requeued");

            ExpireTwice(h, "orders", 22);                                                // dead again at 42
            Text(h.Run(new HwDlqCommand(), At(43), "RESOLVE", ClaimDeadToken(h, 43, "orders"), "KEEP")).Should().Contain("kept");

            Text(h.Run(new HwPublishCommand(), At(50), "news", "{}")).Should().Be(":1\r\n");
            ExpireTwice(h, "news@billing", 50);                                          // dead at 70
            Text(h.Run(new HwDlqCommand(), At(71), "RESOLVE", ClaimDeadToken(h, 71, "news@billing"), "DISCARD")).Should().Contain("discarded");

            Text(h.Run(new HwDlqCommand(), At(72), "PURGE", "Q", "orders", "COUNT", "10")).Should().Be(":1\r\n");
            Text(h.Run(new HwSessionCommand(), At(73), "worker-1", """{"subscriptions":[],"deadLetters":[]}""")).Should().Be("+OK\r\n");
            h.Run(new HwHeartbeatCommand(), At(74), "worker-1", "BYE", "PURGE");
        });

        var finished = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(20)));
        finished.Should().BeSameAs(run, "no command on the dead-letter path may wait on a lock it holds");
        await run;   // surface any assertion inside it
    }

    [Fact]
    public void ADeadLetterAndItsNotification_SurviveARestartTogether()
    {
        var dir = ReplicationToolkitSurfaceTests.NewTempDir();
        try
        {
            using (var store = RocksDbStore.Open(dir))
            using (var h = new CommandHarness(store, Options()))
            {
                Text(h.Run(new HwSessionCommand(), At(0), "worker-1", """{"deadLetters":[{"kind":"Q","name":"orders"}]}""")).Should().Be("+OK\r\n");
                DeadLetterOne(h, "orders", "o-1", 0);
            }

            using (var store = RocksDbStore.Open(dir))
            using (var h = new CommandHarness(store, Options()))
            {
                Text(h.Run(new HwDlqCommand(), At(21), "COUNT", "Q", "orders")).Should().Be(":1\r\n");
                CommandHarness.Claims(h.Run(new HwClaimCommand(), At(21), "DEAD", "Q", "orders", "worker-1"))
                    .Should().ContainSingle("the notification was written in the dead letter's own commit");
            }
        }
        finally { ReplicationToolkitSurfaceTests.TryDelete(dir); }
    }

    [Fact]
    public void AStandby_RefusesTheDeadLetterVerbs()
    {
        var dir = ReplicationToolkitSurfaceTests.NewTempDir();
        try
        {
            using var store = RocksDbStore.Open(dir, replication: new HighwayReplicationOptions { StartAsReplica = true, Priority = 100 });
            var feeder = store.Replication;
            feeder.TryPromote("test", out var error).Should().BeTrue(error);
            feeder.Fence("test");   // a fenced primary is not writable, and is never willing to take the herd
            var options = new HighwayServerOptions();
            var dispatcher = new CommandDispatcher(store, new StripedLock(), new NullDoorbell(),
                new Observability.FlightRecorder(options.Observability), options, replication: feeder);

            foreach (var frame in new[]
            {
                new[] { "HW.CLAIM", "DEAD", "Q", "orders", "worker-1" },
                new[] { "HW.DLQ", "RESOLVE", "0000000100000000000000010000000000000001", "KEEP" },
                new[] { "HW.DLQ", "REQUEUE", "Q", "orders", "COUNT", "1" },
            })
            {
                var writer = new RespWriter();
                dispatcher.Dispatch([.. frame.Select(Encoding.UTF8.GetBytes)], writer);
                Encoding.UTF8.GetString(writer.ToArray()).Should().StartWith("-NOTPRIMARY", string.Join(' ', frame));
            }
        }
        finally { ReplicationToolkitSurfaceTests.TryDelete(dir); }
    }

    private sealed class NullDoorbell : IDoorbell
    {
        public int Ring(string channel, ReadOnlySpan<byte> payload) => 0;
    }
}
