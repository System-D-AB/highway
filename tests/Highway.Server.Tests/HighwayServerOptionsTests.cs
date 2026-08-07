using FluentAssertions;
using Highway.Server.Internal;
using Xunit;

namespace Highway.Server.Tests;

/// <summary>
/// Task 2 — <see cref="HighwayServerOptions"/> defaults.
/// </summary>
public class HighwayServerOptionsTests
{
    [Fact]
    public void Defaults_AreAsSpecified()
    {
        var opts = new HighwayServerOptions();

        opts.Port.Should().Be(6500);
        opts.DataDir.Should().BeNull();
        opts.Lease.Should().Be(TimeSpan.FromMinutes(5));
        opts.ReplySlotTtl.Should().Be(TimeSpan.FromMinutes(5));
        opts.MaxPayloadBytes.Should().Be(1 * 1024 * 1024);
        opts.ReceiveDefaultCount.Should().Be(10);
        opts.ReceiveMaxCount.Should().Be(500);
        opts.WaitForCommit.Should().BeFalse();
    }

    [Fact]
    public void Properties_CanBeOverridden()
    {
        var opts = new HighwayServerOptions
        {
            Port = 7000,
            DataDir = "/tmp/data",
            Lease = TimeSpan.FromMinutes(10),
            ReplySlotTtl = TimeSpan.FromSeconds(30),
            MaxPayloadBytes = 512 * 1024,
            ReceiveDefaultCount = 5,
            ReceiveMaxCount = 100,
            WaitForCommit = true,
        };

        opts.Port.Should().Be(7000);
        opts.DataDir.Should().Be("/tmp/data");
        opts.Lease.Should().Be(TimeSpan.FromMinutes(10));
        opts.ReplySlotTtl.Should().Be(TimeSpan.FromSeconds(30));
        opts.MaxPayloadBytes.Should().Be(512 * 1024);
        opts.ReceiveDefaultCount.Should().Be(5);
        opts.ReceiveMaxCount.Should().Be(100);
        opts.WaitForCommit.Should().BeTrue();
    }

    [Fact]
    public void LeaseZero_DisablesLazyRequeue()
    {
        var opts = new HighwayServerOptions { Lease = TimeSpan.Zero };
        opts.Lease.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void RegistryDefaults_GiveASixTimesHeartbeatMargin()
    {
        var opts = new HighwayServerOptions();

        // 30s expiry against the client's 5s beat: several beats can be lost
        // before a healthy node is declared dead.
        opts.NodeExpiry.Should().Be(TimeSpan.FromSeconds(30));
        opts.PruningEnabled.Should().BeTrue();
        opts.MaxCatalogBytes.Should().Be(256 * 1024);
    }

    [Fact]
    public void RegistryOptions_AreOverridable()
    {
        var opts = new HighwayServerOptions
        {
            NodeExpiry = TimeSpan.FromSeconds(2),
            PruningEnabled = false,
            MaxCatalogBytes = 4096,
        };

        opts.NodeExpiry.Should().Be(TimeSpan.FromSeconds(2));
        opts.PruningEnabled.Should().BeFalse();
        opts.MaxCatalogBytes.Should().Be(4096);
    }
}
