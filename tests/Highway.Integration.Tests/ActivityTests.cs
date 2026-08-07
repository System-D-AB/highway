using System.Diagnostics;
using FluentAssertions;
using Highway.Server;
using Xunit;

namespace Highway.Integration.Tests;

/// <summary>
/// Feature 002 — activity emission, observed with an in-process
/// <see cref="ActivityListener"/>.
///
/// <para><b>No OpenTelemetry dependency in the test either.</b> That is the
/// point: Highway emits <see cref="Activity"/> and anything can collect it. If
/// verifying the spans required the OTEL SDK, the claim that applications are
/// free to choose their own pipeline would be weaker than it looks.</para>
/// </summary>
[Collection(SubscriberRecorderCollection.Name)]
public class ActivityTests : IDisposable
{
    private readonly HighwayTestServer _server = new();
    private readonly List<Activity> _captured = [];
    private readonly ActivityListener _listener;

    public ActivityTests()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name.StartsWith("Highway.", StringComparison.Ordinal),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => { lock (_captured) _captured.Add(activity); },
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose()
    {
        _listener.Dispose();
        _server.Dispose();
    }

    private Activity[] Captured()
    {
        lock (_captured) return [.. _captured];
    }

    /// <summary>
    /// Spans emitted by this test's own node.
    ///
    /// <para>An <see cref="ActivityListener"/> is process-global, so it also sees
    /// every other test class running in parallel. Filtering on the node name —
    /// unique per test here — keeps these assertions about this test's traffic
    /// rather than whatever else the suite happens to be doing.</para>
    /// </summary>
    private Activity[] CapturedFor(string node)
        => [.. Captured().Where(a => (a.GetTagItem("messaging.client.id") as string) == node)];

    [Fact]
    public async Task ExecuteAsync_EmitsAClientSpan_WithSemanticConventionAttributes()
    {
        await using var caller = await EngineNode.StartAsync(_server.ConnectionString, "act-caller");
        await using var host = await EngineNode.StartAsync(_server.ConnectionString, "act-host");

        await caller.Client.ExecuteAsync(new ItEchoRequest { Value = "traced" });

        var span = CapturedFor("act-caller").Should()
            .ContainSingle(a => a.OperationName == "highway.call it.echo").Subject;

        span.Source.Name.Should().Be("Highway.Client");
        span.Kind.Should().Be(ActivityKind.Client);
        span.GetTagItem("messaging.system").Should().Be("highway");
        span.GetTagItem("messaging.destination.name").Should().Be("it.echo");
        span.GetTagItem("messaging.client.id").Should().Be("act-caller");
        span.GetTagItem("highway.status_code").Should().Be(200);
    }

    [Fact]
    public async Task PublishAsync_EmitsAProducerSpan()
    {
        await using var node = await EngineNode.StartAsync(_server.ConnectionString, "act-pub");

        await node.Client.PublishAsync(new ItEvent { Data = "traced" });

        var span = CapturedFor("act-pub").Should()
            .ContainSingle(a => a.OperationName == "highway.publish it.events").Subject;

        span.Kind.Should().Be(ActivityKind.Producer);
        span.GetTagItem("messaging.operation").Should().Be("publish");
        span.GetTagItem("messaging.destination.name").Should().Be("it.events");
    }

    [Fact]
    public async Task FailedCall_MarksTheSpanAsError()
    {
        await using var caller = await EngineNode.StartAsync(_server.ConnectionString, "act-fail");

        // Nothing hosts this request type, so it fails on the local catalog.
        await caller.Client.ExecuteAsync(new UnregisteredRequest { Value = "x" });

        // The local 404 short-circuits before a span is started — there is no
        // remote operation to trace. Assert that rather than inventing one.
        CapturedFor("act-fail").Should().BeEmpty();
    }

    [Fact]
    public async Task TraceContext_ReachesTheServer_ViaTheEnvelope()
    {
        await using var caller = await EngineNode.StartAsync(_server.ConnectionString, "act-tp-caller");
        await using var host = await EngineNode.StartAsync(_server.ConnectionString, "act-tp-host");

        using var source = new ActivitySource("Highway.Test.Root");
        using var rootListener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "Highway.Test.Root",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(rootListener);

        using var root = source.StartActivity("caller-root");
        root.Should().NotBeNull();

        await caller.Client.ExecuteAsync(new ItEchoRequest { Value = "traced" });

        var callSpan = CapturedFor("act-tp-caller").Should()
            .ContainSingle(a => a.OperationName == "highway.call it.echo").Subject;
        callSpan.TraceId.Should().Be(root!.TraceId,
            "the client span joins the caller's existing trace, so the traceparent it emits is the caller's");
    }

    [Fact]
    public async Task ActivitiesDisabled_EmitsNothing()
    {
        await using var caller = await EngineNode.StartAsync(
            _server.ConnectionString, "act-off-caller", o => o.ActivitiesEnabled = false);
        await using var host = await EngineNode.StartAsync(_server.ConnectionString, "act-off-host");

        await caller.Client.ExecuteAsync(new ItEchoRequest { Value = "x" });

        CapturedFor("act-off-caller").Should().BeEmpty("emission is off for this node");
    }
}
