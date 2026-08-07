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
        TestSubscriber.Reset();
        var del = _compiler.CompileSubscriberDelegate(typeof(TestSubscriber), typeof(TestEvent));

        var subscriber = new TestSubscriber();
        var message = new TestEvent { Data = "test-data" };

        await del(subscriber, message, CancellationToken.None);

        TestSubscriber.CallCount.Should().Be(1);
        TestSubscriber.LastData.Should().Be("test-data");
    }

    [Fact]
    public async Task CompileSubscriberDelegate_MultipleInvocations_Accumulate()
    {
        TestSubscriber.Reset();
        var del = _compiler.CompileSubscriberDelegate(typeof(TestSubscriber), typeof(TestEvent));

        var subscriber = new TestSubscriber();

        await del(subscriber, new TestEvent { Data = "first" }, CancellationToken.None);
        await del(subscriber, new TestEvent { Data = "second" }, CancellationToken.None);

        TestSubscriber.CallCount.Should().Be(2);
        TestSubscriber.LastData.Should().Be("second");
    }

    [Fact]
    public void CompileServiceDelegate_ReturnsDifferentDelegatesPerType()
    {
        var del1 = _compiler.CompileServiceDelegate(typeof(TestService), typeof(TestRequest), typeof(TestResponse));
        var del2 = _compiler.CompileServiceDelegate(typeof(ThrowingService), typeof(ThrowingRequest), typeof(TestResponse));

        del1.Should().NotBeSameAs(del2);
    }
}
