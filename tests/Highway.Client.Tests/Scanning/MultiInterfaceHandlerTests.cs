using FluentAssertions;
using Highway.Abstractions;
using Highway.Client.Scanning;
using Xunit;

namespace Highway.Client.Tests.Scanning;

/// <summary>
/// F1 — the scanner must register <b>every</b> closed <c>ISubscribe&lt;&gt;</c> and
/// <c>IProcess&lt;&gt;</c> interface, not the first (concerns.md 5.6; both architecture reviews
/// found this independently).
///
/// <para>The defect: <c>GetInterfaces().FirstOrDefault(...)</c>. A class listening to two
/// channels silently heard one; a class processing two queues silently processed one. No
/// error anywhere — the dropped queue's messages simply waited for a processor that never
/// came, and the dropped channel never fired. Writing one handler class for several related
/// events is the natural thing to do at 10+ channels, which is exactly why the failure mode
/// mattered.</para>
/// </summary>
public class MultiInterfaceHandlerTests
{
    private readonly DefaultTypeScanner _scanner = new();

    [Fact]
    public void Subscriber_WithTwoInterfaces_IsRegisteredOnBothChannels()
    {
        var result = _scanner.ScanTypes([typeof(MiBothSubscriber)]);

        // With FirstOrDefault this was ContainSingle — whichever interface reflection
        // happened to order first — and nothing said so.
        result.Channels.Should().HaveCount(2);

        result.Channels.Should().Contain(c => c.Name == "mi.first")
            .Which.Subscribers.Should().ContainSingle(s => s.ImplementationType == typeof(MiBothSubscriber));

        result.Channels.Should().Contain(c => c.Name == "mi.second")
            .Which.Subscribers.Should().ContainSingle(s => s.ImplementationType == typeof(MiBothSubscriber));
    }

    [Fact]
    public void Processor_WithTwoInterfaces_IsRegisteredOnBothQueues()
    {
        var result = _scanner.ScanTypes([typeof(MiBothProcessor)]);

        result.Queues.Should().HaveCount(2);
        result.Queues.Should().Contain(q => q.Name == "mi.q1" && q.ProcessorType == typeof(MiBothProcessor));
        result.Queues.Should().Contain(q => q.Name == "mi.q2" && q.ProcessorType == typeof(MiBothProcessor));
    }

    [Fact]
    public void Processor_WithTwoInterfaces_StillCollidesWithASecondProcessorForTheSameQueue()
    {
        // The multi-interface fix must not weaken the exactly-one-processor rule.
        var act = () => _scanner.ScanTypes([typeof(MiBothProcessor), typeof(MiRivalProcessor)]);

        act.Should().Throw<Exception>("mi.q1 would have two competing processor classes");
    }
}

// --- fixtures ---------------------------------------------------------------

[Channel("mi.first")]
public sealed class MiFirstEvent : IPublish;

[Channel("mi.second")]
public sealed class MiSecondEvent : IPublish;

/// <summary>One class, two subscriptions — the natural shape the scanner used to truncate.</summary>
public sealed class MiBothSubscriber : ISubscribe<MiFirstEvent>, ISubscribe<MiSecondEvent>
{
    public Task SubscribeAsync(MiFirstEvent message, CancellationToken ct = default) => Task.CompletedTask;
    public Task SubscribeAsync(MiSecondEvent message, CancellationToken ct = default) => Task.CompletedTask;
}

[Queue("mi.q1")]
public sealed class MiWorkOne : ISend;

[Queue("mi.q2")]
public sealed class MiWorkTwo : ISend;

public sealed class MiBothProcessor : IProcess<MiWorkOne>, IProcess<MiWorkTwo>
{
    public Task ProcessAsync(MiWorkOne message, CancellationToken ct = default) => Task.CompletedTask;
    public Task ProcessAsync(MiWorkTwo message, CancellationToken ct = default) => Task.CompletedTask;
}

public sealed class MiRivalProcessor : IProcess<MiWorkOne>
{
    public Task ProcessAsync(MiWorkOne message, CancellationToken ct = default) => Task.CompletedTask;
}
