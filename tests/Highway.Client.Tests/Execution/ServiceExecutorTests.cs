using FluentAssertions;
using Highway.Abstractions;
using Highway.Client.Execution;
using Highway.Client.Scanning;
using Highway.Client.Tests.TestFixtures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

using ServiceDescriptor = Highway.Client.Scanning.ServiceDescriptor;

namespace Highway.Client.Tests.Execution;

public class ServiceExecutorTests : IDisposable
{
    private readonly ServiceProvider _provider;
    private readonly ServiceExecutor _executor;

    public ServiceExecutorTests()
    {
        var compiler = new ExpressionDelegateCompiler();

        var services = new List<ServiceDescriptor>
        {
            new()
            {
                Name = "test.hello",
                ImplementationType = typeof(TestService),
                RequestType = typeof(TestRequest),
                ResponseType = typeof(TestResponse),
                Lifetime = HighwayServiceLifetime.Scoped,
                InvokeDelegate = compiler.CompileServiceDelegate(typeof(TestService), typeof(TestRequest), typeof(TestResponse))
            },
            new()
            {
                Name = "test.throws",
                ImplementationType = typeof(ThrowingService),
                RequestType = typeof(ThrowingRequest),
                ResponseType = typeof(TestResponse),
                Lifetime = HighwayServiceLifetime.Scoped,
                InvokeDelegate = compiler.CompileServiceDelegate(typeof(ThrowingService), typeof(ThrowingRequest), typeof(TestResponse))
            }
        };

        var channels = new List<ChannelDescriptor>
        {
            new()
            {
                Name = "test.events",
                MessageType = typeof(TestEvent),
                Subscribers = new List<SubscriberDescriptor>
                {
                    new()
                    {
                        ImplementationType = typeof(TestSubscriber),
                        Lifetime = HighwayServiceLifetime.Scoped,
                        InvokeDelegate = compiler.CompileSubscriberDelegate(typeof(TestSubscriber), typeof(TestEvent))
                    },
                    new()
                    {
                        ImplementationType = typeof(TestSubscriber2),
                        Lifetime = HighwayServiceLifetime.Scoped,
                        InvokeDelegate = compiler.CompileSubscriberDelegate(typeof(TestSubscriber2), typeof(TestEvent))
                    }
                }
            }
        };

        var catalog = new ImmutableCatalog(services, channels);

        var serviceCollection = new ServiceCollection();
        serviceCollection.AddScoped<TestService>();
        serviceCollection.AddScoped<ThrowingService>();
        serviceCollection.AddScoped<TestSubscriber>();
        serviceCollection.AddScoped<TestSubscriber2>();
        _provider = serviceCollection.BuildServiceProvider();

        _executor = new ServiceExecutor(catalog, _provider.GetRequiredService<IServiceScopeFactory>());
    }

    [Fact]
    public async Task ExecuteServiceAsync_ReturnsCorrectResponse()
    {
        var request = new TestRequest { Input = "World" };
        var result = await _executor.ExecuteServiceAsync("test.hello", request);

        result.Should().BeOfType<TestResponse>();
        var response = (TestResponse)result;
        response.Value.Should().Be("Hello World");
        response.StatusCode.Should().Be(200);
    }

    [Fact]
    public async Task ExecuteServiceAsync_UnknownService_Returns404()
    {
        var request = new TestRequest { Input = "World" };
        var result = await _executor.ExecuteServiceAsync("nonexistent", request);

        result.Should().BeOfType<GenericOutput>();
        var output = (GenericOutput)result;
        output.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        output.Error.Should().NotBeNull();
        output.Error!.Code.Should().Be("SERVICE_NOT_FOUND");
    }

    [Fact]
    public async Task ExecuteServiceAsync_ServiceThrows_Returns500()
    {
        var request = new ThrowingRequest { Message = "Oops" };
        var result = await _executor.ExecuteServiceAsync("test.throws", request);

        result.Should().BeOfType<GenericOutput>();
        var output = (GenericOutput)result;
        output.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
        output.Error.Should().NotBeNull();
        output.Error!.Code.Should().Be("SERVICE_EXCEPTION");
        output.Error.Message.Should().Be("Oops");
    }

    [Fact]
    public async Task ExecuteServiceAsync_CaseInsensitiveServiceName()
    {
        var request = new TestRequest { Input = "Case" };
        var result = await _executor.ExecuteServiceAsync("TEST.HELLO", request);

        result.Should().BeOfType<TestResponse>();
        ((TestResponse)result).Value.Should().Be("Hello Case");
    }

    [Fact]
    public async Task ExecuteSubscribersAsync_InvokesAllSubscribers()
    {
        TestSubscriber.Reset();
        TestSubscriber2.Reset();

        var message = new TestEvent { Data = "hello" };
        var response = await _executor.ExecuteSubscribersAsync("test.events", message);

        response.TotalSubscribers.Should().Be(2);
        response.SuccessCalls.Should().Be(2);
        TestSubscriber.CallCount.Should().Be(1);
        TestSubscriber2.CallCount.Should().Be(1);
        TestSubscriber.LastData.Should().Be("hello");
    }

    [Fact]
    public async Task ExecuteSubscribersAsync_UnknownChannel_ReturnsZero()
    {
        var message = new TestEvent { Data = "hello" };
        var response = await _executor.ExecuteSubscribersAsync("nonexistent", message);

        response.TotalSubscribers.Should().Be(0);
        response.SuccessCalls.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteServiceAsync_NoDelegateCompiled_Returns500()
    {
        // Build catalog with service missing InvokeDelegate
        var services = new List<ServiceDescriptor>
        {
            new()
            {
                Name = "test.no-delegate",
                ImplementationType = typeof(TestService),
                RequestType = typeof(TestRequest),
                ResponseType = typeof(TestResponse),
                Lifetime = HighwayServiceLifetime.Scoped,
                InvokeDelegate = null
            }
        };
        var catalog = new ImmutableCatalog(services, []);
        var executor = new ServiceExecutor(catalog, _provider.GetRequiredService<IServiceScopeFactory>());

        var request = new TestRequest { Input = "test" };
        var result = await executor.ExecuteServiceAsync("test.no-delegate", request);

        result.Should().BeOfType<GenericOutput>();
        var output = (GenericOutput)result;
        output.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
        output.Error!.Code.Should().Be("NO_DELEGATE");
    }

    [Fact]
    public async Task ExecuteServiceAsync_Cancelled_Returns408()
    {
        // TestService completes synchronously via Task.FromResult, so cancellation
        // only triggers if the service actually observes the token.
        // We test this by adding a service that respects cancellation.
        var compiler = new ExpressionDelegateCompiler();
        var services = new List<ServiceDescriptor>
        {
            new()
            {
                Name = "test.cancellable",
                ImplementationType = typeof(CancellableService),
                RequestType = typeof(CancellableRequest),
                ResponseType = typeof(TestResponse),
                Lifetime = HighwayServiceLifetime.Scoped,
                InvokeDelegate = compiler.CompileServiceDelegate(typeof(CancellableService), typeof(CancellableRequest), typeof(TestResponse))
            }
        };
        var catalog = new ImmutableCatalog(services, []);

        var serviceCollection = new ServiceCollection();
        serviceCollection.AddScoped<CancellableService>();
        using var provider = serviceCollection.BuildServiceProvider();
        var executor = new ServiceExecutor(catalog, provider.GetRequiredService<IServiceScopeFactory>());

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var request = new CancellableRequest();
        var result = await executor.ExecuteServiceAsync("test.cancellable", request, cts.Token);

        result.Should().BeOfType<GenericOutput>();
        var output = (GenericOutput)result;
        output.StatusCode.Should().Be(StatusCodes.Status408RequestTimeout);
        output.Error!.Code.Should().Be("CANCELLED");
    }

    public void Dispose()
    {
        _provider.Dispose();
    }
}
