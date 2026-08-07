using Highway.Abstractions;

namespace Highway.Client.Tests.TestFixtures;

// --- Valid service ---
public sealed class TestResponse : Output
{
    public string? Value { get; set; }
}

[Service("test.hello")]
public sealed class TestRequest : IReturn<TestResponse>
{
    public string? Input { get; set; }
}

public sealed class TestService : AsyncService<TestRequest, TestResponse>
{
    public override Task<TestResponse> ExecuteAsync(TestRequest request, CancellationToken ct = default)
    {
        return Task.FromResult(new TestResponse
        {
            Value = $"Hello {request.Input}",
            StatusCode = StatusCodes.Status200OK
        });
    }
}

// --- Valid channel + subscribers ---
[Channel("test.events")]
public sealed class TestEvent : IPublish
{
    public string? Data { get; set; }
}

public sealed class TestSubscriber : ISubscribe<TestEvent>
{
    public static int CallCount;
    public static string? LastData;

    public Task SubscribeAsync(TestEvent message, CancellationToken ct = default)
    {
        Interlocked.Increment(ref CallCount);
        LastData = message.Data;
        return Task.CompletedTask;
    }

    public static void Reset() { CallCount = 0; LastData = null; }
}

public sealed class TestSubscriber2 : ISubscribe<TestEvent>
{
    public static int CallCount;

    public Task SubscribeAsync(TestEvent message, CancellationToken ct = default)
    {
        Interlocked.Increment(ref CallCount);
        return Task.CompletedTask;
    }

    public static void Reset() { CallCount = 0; }
}

// --- Consumer-loop subscribers ---
// Dedicated types so the loop tests never share static counters with
// ServiceExecutorTests: xUnit runs test classes in parallel.

public sealed class LoopSubscriberA : ISubscribe<TestEvent>
{
    public static int CallCount;
    public static string? LastData;

    public Task SubscribeAsync(TestEvent message, CancellationToken ct = default)
    {
        Interlocked.Increment(ref CallCount);
        LastData = message.Data;
        return Task.CompletedTask;
    }

    public static void Reset() { CallCount = 0; LastData = null; }
}

public sealed class LoopSubscriberB : ISubscribe<TestEvent>
{
    public static int CallCount;

    public Task SubscribeAsync(TestEvent message, CancellationToken ct = default)
    {
        Interlocked.Increment(ref CallCount);
        return Task.CompletedTask;
    }

    public static void Reset() { CallCount = 0; }
}

/// <summary>A message with no [Channel] registration — exercises the catalog-miss path.</summary>
public sealed class UnregisteredEvent : IPublish
{
    public string? Data { get; set; }
}

/// <summary>Subscriber that always throws — proves sibling isolation.</summary>
public sealed class ThrowingSubscriber : ISubscribe<TestEvent>
{
    public static int CallCount;

    public Task SubscribeAsync(TestEvent message, CancellationToken ct = default)
    {
        Interlocked.Increment(ref CallCount);
        throw new InvalidOperationException("subscriber boom");
    }

    public static void Reset() { CallCount = 0; }
}

// --- Service that throws ---
[Service("test.throws")]
public sealed class ThrowingRequest : IReturn<TestResponse>
{
    public string? Message { get; set; }
}

public sealed class ThrowingService : AsyncService<ThrowingRequest, TestResponse>
{
    public override Task<TestResponse> ExecuteAsync(ThrowingRequest request, CancellationToken ct = default)
    {
        throw new InvalidOperationException(request.Message ?? "Boom!");
    }
}

// --- Service that respects cancellation ---
[Service("test.cancellable")]
public sealed class CancellableRequest : IReturn<TestResponse>;

public sealed class CancellableService : AsyncService<CancellableRequest, TestResponse>
{
    public override async Task<TestResponse> ExecuteAsync(CancellableRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await Task.Delay(1000, ct);
        return new TestResponse { Value = "done", StatusCode = StatusCodes.Status200OK };
    }
}
