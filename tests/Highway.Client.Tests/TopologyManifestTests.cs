using FluentAssertions;
using Highway.Abstractions;
using Highway.Client;
using Highway.Client.Scanning;
using Highway.Client.Tests.TestFixtures;
using Xunit;

namespace Highway.Client.Tests;

/// <summary>
/// Feature 024 T5 — the manifest answers "what does this deployment do?" from the scan, and
/// never overclaims: the consumption half is <b>can use</b>, because a referenced contract
/// proves addressability, not calling.
/// </summary>
public class TopologyManifestTests
{
    private static TopologyManifest Build()
    {
        var scanner = new DefaultTypeScanner();
        var scan = scanner.ScanTypes(
        [
            typeof(TestRequest), typeof(TestService),        // hosted RPC: test.hello
            typeof(TestEvent), typeof(TestSubscriber),       // hosted subscription: test.events
            typeof(MfRemoteOnlyRequest),                     // contract with NO local host
        ]);

        var catalog = new ImmutableCatalog(
            scan.Services, scan.Channels, scan.RequestContracts,
            scan.MessageContracts, scan.Queues, scan.QueueContracts);

        return TopologyManifest.Build("node-1", catalog, scan);
    }

    [Fact]
    public void Provides_NamesRouteImplementationAndSourceAssembly()
    {
        var manifest = Build();

        var rpc = manifest.Provides.Should()
            .ContainSingle(p => p.Kind == CapabilityKind.RpcService).Subject;

        rpc.Route.Should().Be("test.hello");
        rpc.ImplementationType.Should().Be(nameof(TestService));
        rpc.SourceAssembly.Should().Be(typeof(TestService).Assembly.GetName().Name,
            "'where did this handler come from?' is answered by the line that announces it");
    }

    [Fact]
    public void SubscriberEntries_CarryTheirGroup()
    {
        var sub = Build().Provides.Should()
            .ContainSingle(p => p.Kind == CapabilityKind.Subscriber).Subject;

        sub.Route.Should().Be("test.events");
        sub.Group.Should().Be("node-1", "the group is the node name until feature 025");
    }

    [Fact]
    public void CanUse_ListsRemoteContracts_AndExcludesWhatThisProcessProvides()
    {
        var canUse = Build().CanUse;

        canUse.Services.Should().Contain("mf.remote", "the contract is referenced, so it is addressable");
        canUse.Services.Should().NotContain("test.hello",
            "the interesting half of topology is what the process reaches OUT to");
    }

    [Fact]
    public void LogString_LabelsTheConsumptionHalfHonestly()
    {
        var text = Build().ToLogString();

        text.Should().Contain("CAN USE (references the contract; not proof of calling)",
            "the manifest must not overclaim (R3.2)");
        text.Should().Contain("group=node-1");
        text.Should().Contain("test.hello");
    }
}

/// <summary>A [Service] contract this test assembly deliberately does not host.</summary>
[Service("mf.remote")]
public sealed class MfRemoteOnlyRequest : IReturn<TestResponse>;
