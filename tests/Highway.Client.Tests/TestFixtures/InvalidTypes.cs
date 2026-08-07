using Highway.Abstractions;
using Highway.Client.Tests.TestFixtures;

namespace Highway.Client.Tests.TestFixtures.Invalid;

// Missing [Service] attribute on request
public sealed class NoAttributeRequest : IReturn<TestResponse>;

public sealed class NoAttributeService : AsyncService<NoAttributeRequest, TestResponse>
{
    public override Task<TestResponse> ExecuteAsync(NoAttributeRequest request, CancellationToken ct = default)
        => Task.FromResult(new TestResponse());
}

// Missing [Channel] attribute on message
public sealed class NoChannelMessage : IPublish;

public sealed class NoChannelSubscriber : ISubscribe<NoChannelMessage>
{
    public Task SubscribeAsync(NoChannelMessage message, CancellationToken ct = default)
        => Task.CompletedTask;
}

// Duplicate service name (same as TestRequest's "test.hello")
[Service("test.hello")]
public sealed class DuplicateRequest : IReturn<TestResponse>;

public sealed class DuplicateService : AsyncService<DuplicateRequest, TestResponse>
{
    public override Task<TestResponse> ExecuteAsync(DuplicateRequest request, CancellationToken ct = default)
        => Task.FromResult(new TestResponse());
}

// Response type without a public parameterless constructor (feature 005, Task 3)
public sealed class NoParameterlessCtorResponse : Output
{
    public NoParameterlessCtorResponse(string required) => Required = required;
    public string Required { get; }
}

[Service("test.noctor")]
public sealed class NoParameterlessCtorRequest : IReturn<NoParameterlessCtorResponse>;

public sealed class NoParameterlessCtorService : AsyncService<NoParameterlessCtorRequest, NoParameterlessCtorResponse>
{
    public override Task<NoParameterlessCtorResponse> ExecuteAsync(NoParameterlessCtorRequest request, CancellationToken ct = default)
        => Task.FromResult(new NoParameterlessCtorResponse("x"));
}
