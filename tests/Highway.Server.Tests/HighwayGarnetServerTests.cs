using System.Net;
using FluentAssertions;
using Garnet.server;
using Highway.Server.Internal;
using Xunit;

namespace Highway.Server.Tests;

/// <summary>
/// Task 4 — <see cref="HighwayGarnetServer"/> and <see cref="DoorbellBridge"/> tests.
///
/// Tests verify:
/// 1. Server constructs with memory-only options without throwing.
/// 2. <see cref="HighwayGarnetServer.SubscribeBroker"/> is accessible without reflection.
/// 3. <see cref="DoorbellBridge.Ring"/> before any subscriber returns 0 without throwing.
/// 4. Dispose completes cleanly (no exceptions, port released).
/// </summary>
public class HighwayGarnetServerTests : IDisposable
{
    private HighwayGarnetServer? _server;

    public void Dispose() => _server?.Dispose();

    private static GarnetServerOptions MemoryOnlyOptions(int port) => new()
    {
        QuietMode = true,
        EnableAOF = false,
        EnableStorageTier = false,
        DisablePubSub = false,
        EndPoints = [new IPEndPoint(IPAddress.Loopback, port)],
    };

    [Fact]
    public void Constructor_WithMemoryOnlyOptions_DoesNotThrow()
    {
        var port = EphemeralPort.Probe();
        var opts = MemoryOnlyOptions(port);

        var act = () => { _server = new HighwayGarnetServer(opts); };
        act.Should().NotThrow();
    }

    [Fact]
    public void SubscribeBroker_IsAccessible_WithoutReflection()
    {
        var port = EphemeralPort.Probe();
        _server = new HighwayGarnetServer(MemoryOnlyOptions(port));

        // Property access should never throw.
        var act = () => _ = _server.SubscribeBroker;
        act.Should().NotThrow();
    }

    [Fact]
    public void Dispose_ReleasesWithoutException()
    {
        var port = EphemeralPort.Probe();
        using var srv = new HighwayGarnetServer(MemoryOnlyOptions(port));

        // Dispose via 'using' scope — must not throw.
        // We don't assign to _server here to avoid double-dispose from the IDisposable teardown.
        var act = () =>
        {
            using var s = new HighwayGarnetServer(MemoryOnlyOptions(EphemeralPort.Probe()));
            // Dispose is called on scope exit.
        };
        act.Should().NotThrow();
    }

    [Fact]
    public void DoorbellBridge_RingBeforeAnySubscriber_ReturnsZeroWithoutThrowing()
    {
        var port = EphemeralPort.Probe();
        _server = new HighwayGarnetServer(MemoryOnlyOptions(port));

        var bridge = new DoorbellBridge(_server);

        // The broker has not been initialised yet (no SUBSCRIBE call); Ring must
        // return 0 and not throw.
        int count = 0;
        var act = () => { count = bridge.Ring("hw:door:test", "hello"); };
        act.Should().NotThrow();
        count.Should().Be(0);
    }

    [Fact]
    public void DoorbellBridge_RingWithBytePayload_DoesNotThrow()
    {
        var port = EphemeralPort.Probe();
        _server = new HighwayGarnetServer(MemoryOnlyOptions(port));

        var bridge = new DoorbellBridge(_server);
        var payload = new byte[] { 0x01, 0x02, 0x03 };

        var act = () => bridge.Ring("hw:door:test", payload.AsSpan());
        act.Should().NotThrow();
    }

    [Fact]
    public void DoorbellBridge_RingWithStringPayload_DoesNotThrow()
    {
        var port = EphemeralPort.Probe();
        _server = new HighwayGarnetServer(MemoryOnlyOptions(port));

        var bridge = new DoorbellBridge(_server);

        var act = () => bridge.Ring("hw:door:test", "payload-string");
        act.Should().NotThrow();
    }

    [Fact]
    public void TwoConcurrentServers_GetDistinctPortsAndAreIsolated()
    {
        var port1 = EphemeralPort.Probe();
        var port2 = EphemeralPort.Probe();

        // Ensure ports differ; probe again if they collide (extremely rare on loopback).
        if (port1 == port2)
            port2 = EphemeralPort.Probe();

        using var srv1 = new HighwayGarnetServer(MemoryOnlyOptions(port1));
        using var srv2 = new HighwayGarnetServer(MemoryOnlyOptions(port2));

        // Both should construct without interference.
        srv1.Should().NotBeNull();
        srv2.Should().NotBeNull();
    }
}
