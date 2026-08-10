using System.Reflection;
using FluentAssertions;
using Highway.Abstractions;
using Highway.Client.Scanning;
using Highway.Client.Tests.TestFixtures;
using Xunit;

namespace Highway.Client.Tests.Scanning;

/// <summary>
/// Feature 024 — <b>contracts by closure, handlers by consent.</b>
///
/// <para>The rule under test: contract discovery keeps its full reach in every mode (that
/// reach fixed the caller-only <c>SERVICE_NOT_FOUND</c> defect), while handler discovery is
/// limited to assemblies the process consented to host — and whatever is excluded is
/// <i>reported</i>, never silently dropped.</para>
/// </summary>
public class HostingBoundaryTests
{
    private readonly DefaultTypeScanner _scanner = new();

    private static readonly Assembly Self = typeof(HostingBoundaryTests).Assembly;
    private static readonly Assembly Other = typeof(IHighwayClient).Assembly; // no [HighwayHostModule]

    // ---- the partition ------------------------------------------------------

    [Fact]
    public void HandlerInContractOnlySet_IsNotHosted_ButItsContractIs()
    {
        // TestService hosts test.hello; TestRequest carries the [Service] contract.
        var result = _scanner.ScanTypes(
            contractTypes: [typeof(TestRequest), typeof(TestService)],
            handlerTypes: []);

        result.Services.Should().BeEmpty("no assembly consented to host");
        result.RequestContracts.Should().ContainKey(typeof(TestRequest),
            "contract discovery must keep its full reach in every mode — " +
            "a caller-only process still addresses every route it references");
    }

    [Fact]
    public void HandlerInBothSets_IsHostedExactlyAsBefore()
    {
        var types = new[] { typeof(TestRequest), typeof(TestService) };
        var result = _scanner.ScanTypes(contractTypes: types, handlerTypes: types);

        result.Services.Should().ContainSingle(s => s.Name == "test.hello");
    }

    // ---- mode selection -----------------------------------------------------

    [Fact]
    public void Implicit_HostsEveryScannedAssembly()
    {
        HostingBoundary.SelectHandlerAssemblies(
                HostingMode.Implicit, [Self, Other], entryAssembly: null, declared: [])
            .Should().Equal(Self, Other);
    }

    [Fact]
    public void Declared_HostsEntryAndDeclaredOnly()
    {
        HostingBoundary.SelectHandlerAssemblies(
                HostingMode.Declared, [Self, Other], entryAssembly: Self, declared: [])
            .Should().Equal(new[] { Self }, "the entry assembly hosts by default in Declared mode");

        HostingBoundary.SelectHandlerAssemblies(
                HostingMode.Declared, [Self, Other], entryAssembly: Self, declared: [Other])
            .Should().Equal(new[] { Self, Other }, "HostAssembly(...) is the composition root's consent");
    }

    [Fact]
    public void ExplicitOnly_IgnoresEvenTheEntryAssembly()
    {
        HostingBoundary.SelectHandlerAssemblies(
                HostingMode.ExplicitOnly, [Self, Other], entryAssembly: Self, declared: [])
            .Should().BeEmpty("ExplicitOnly means every hosting decision is written down");

        HostingBoundary.SelectHandlerAssemblies(
                HostingMode.ExplicitOnly, [Self, Other], entryAssembly: Self, declared: [Self])
            .Should().Equal(Self);
    }

    // ---- the skip report (R1.5) --------------------------------------------

    [Fact]
    public void SkippedHandlers_AreNamed_NotSilentlyDropped()
    {
        var skipped = DefaultTypeScanner.DiscoverSkippedFromTypes(
            [typeof(TestService), typeof(TestSubscriber), typeof(TestRequest)]);

        var entry = skipped.Should().ContainSingle().Subject;
        entry.AssemblyName.Should().Be(Self.GetName().Name);
        entry.HandlerTypes.Should().Contain(typeof(TestService).FullName)
            .And.Contain(typeof(TestSubscriber).FullName)
            .And.NotContain(typeof(TestRequest).FullName, "a contract is not a handler");
    }

    [Fact]
    public void SkipDetection_IsStructural_AndDoesNotValidate()
    {
        // A type that LOOKS like a processor but whose message type has no [Queue] attribute:
        // full discovery would throw. The skip report must only NAME it — an excluded
        // assembly's mistakes are not this process's errors.
        var act = () => DefaultTypeScanner.DiscoverSkippedFromTypes([typeof(InvalidProcessor)]);

        act.Should().NotThrow().Which.Should().ContainSingle()
            .Which.HandlerTypes.Should().Contain(typeof(InvalidProcessor).FullName);
    }

    // ---- the Implicit-mode accident report (R2) ----------------------------

    [Fact]
    public void ReferenceHostedAssemblies_NamesTheUnconsentedContributor()
    {
        var scan = _scanner.ScanTypes([typeof(TestRequest), typeof(TestService)]);

        var hosted = HostingBoundary.ReferenceHostedAssemblies(
            scan, entryAssembly: Other, declared: []);

        var entry = hosted.Should().ContainSingle("the fixture assembly is neither entry nor declared").Subject;
        entry.AssemblyName.Should().Be(Self.GetName().Name);
        entry.HandlerTypes.Should().Contain(typeof(TestService).FullName);
    }

    [Fact]
    public void ReferenceHostedAssemblies_IsQuiet_WhenHandlersLiveInTheEntryAssembly()
    {
        var scan = _scanner.ScanTypes([typeof(TestRequest), typeof(TestService)]);

        HostingBoundary.ReferenceHostedAssemblies(scan, entryAssembly: Self, declared: [])
            .Should().BeEmpty("the samples must boot without warnings (R2.3)");

        HostingBoundary.ReferenceHostedAssemblies(scan, entryAssembly: Other, declared: [Self])
            .Should().BeEmpty("declaration is consent");
    }
}

/// <summary>Looks like a processor; would fail full discovery (message type lacks [Queue]).</summary>
public sealed class InvalidProcessor : IProcess<UnattributedWork>
{
    public Task ProcessAsync(UnattributedWork message, CancellationToken ct = default) => Task.CompletedTask;
}

public sealed class UnattributedWork : ISend;
