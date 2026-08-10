using FluentAssertions;
using Highway.Client.Scanning;
using Highway.Client.Tests.TestFixtures;
using Xunit;

namespace Highway.Client.Tests.Scanning;

public class DelegateCompilerTests
{
    private readonly ExpressionDelegateCompiler _compiler = new();

    [Fact]
    public async Task CompileServiceDelegate_InvokesCorrectly()
    {
        var del = _compiler.CompileServiceDelegate(typeof(TestService), typeof(TestRequest), typeof(TestResponse));

        var service = new TestService();
        var request = new TestRequest { Input = "World" };

        var result = await del(service, request, CancellationToken.None);

        result.Should().BeOfType<TestResponse>();
        var response = (TestResponse)result;
        response.Value.Should().Be("Hello World");
    }

    [Fact]
    public async Task CompileServiceDelegate_PropagatesExceptions()
    {
        var del = _compiler.CompileServiceDelegate(typeof(ThrowingService), typeof(ThrowingRequest), typeof(TestResponse));

        var service = new ThrowingService();
        var request = new ThrowingRequest { Message = "Test error" };

        var act = async () => await del(service, request, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Test error");
    }

    [Fact]
    public async Task CompileSubscriberDelegate_InvokesCorrectly()
    {
        CompilerOnlySubscriber.Reset();
        var del = _compiler.CompileSubscriberDelegate(typeof(CompilerOnlySubscriber), typeof(TestEvent));

        var subscriber = new CompilerOnlySubscriber();
        var message = new TestEvent { Data = "test-data" };

        await del(subscriber, message, CancellationToken.None);

        CompilerOnlySubscriber.CallCount.Should().Be(1);
        CompilerOnlySubscriber.LastData.Should().Be("test-data");
    }

    [Fact]
    public async Task CompileSubscriberDelegate_MultipleInvocations_Accumulate()
    {
        CompilerOnlySubscriber.Reset();
        var del = _compiler.CompileSubscriberDelegate(typeof(CompilerOnlySubscriber), typeof(TestEvent));

        var subscriber = new CompilerOnlySubscriber();

        await del(subscriber, new TestEvent { Data = "first" }, CancellationToken.None);
        await del(subscriber, new TestEvent { Data = "second" }, CancellationToken.None);

        CompilerOnlySubscriber.CallCount.Should().Be(2);
        CompilerOnlySubscriber.LastData.Should().Be("second");
    }

    [Fact]
    public void CompileServiceDelegate_ReturnsDifferentDelegatesPerType()
    {
        var del1 = _compiler.CompileServiceDelegate(typeof(TestService), typeof(TestRequest), typeof(TestResponse));
        var del2 = _compiler.CompileServiceDelegate(typeof(ThrowingService), typeof(ThrowingRequest), typeof(TestResponse));

        del1.Should().NotBeSameAs(del2);
    }

/// <summary>
/// Dedicated to this class. It used to share <c>TestSubscriber</c>'s static counters with
/// <c>ServiceExecutorTests</c> — two classes xUnit runs in PARALLEL — which produced a
/// once-per-many-runs flake (first seen unattributed during 024, attributed during 025).
/// Same rule as the loop tests' dedicated types, for the same reason.
/// </summary>
public sealed class CompilerOnlySubscriber : Highway.Abstractions.ISubscribe<TestFixtures.TestEvent>
{
    public static int CallCount;
    public static string? LastData;

    public Task SubscribeAsync(TestFixtures.TestEvent message, CancellationToken ct = default)
    {
        CallCount++;
        LastData = message.Data;
        return Task.CompletedTask;
    }

    public static void Reset() { CallCount = 0; LastData = null; }
}
}
