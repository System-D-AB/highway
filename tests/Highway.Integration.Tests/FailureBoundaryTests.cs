using System.Collections.Concurrent;
using FluentAssertions;
using Highway.Abstractions;
using Highway.Server;
using Xunit;

namespace Highway.Integration.Tests;

/// <summary>
/// Deterministic failure boundaries (concerns.md 9.1 / 9.2, fixed on user instruction without a
/// feature spec).
///
/// <para>Both defects had the same shape: a failure the first attempt fully understood was
/// reported as if the broker were misbehaving. A constructor throw escaped the RPC error
/// mapping, so a <b>permanent configuration error</b> became lease recovery and a caller
/// timeout. An unserializable response threw <i>after</i> the handler succeeded but before the
/// ack, so the completed business operation was <b>re-run</b> on redelivery.</para>
///
/// <para>The tests assert the world these fixes create: the caller receives a prompt, typed,
/// named error — and for the serialization case, the handler runs exactly once.</para>
/// </summary>
public class FailureBoundaryTests : IAsyncLifetime
{
    private readonly HighwayTestServer _server = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        _server.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task UnserializableResponse_RepliesTypedError_AndDoesNotRerunTheHandler()
    {
        FbCyclicService.Invocations.Clear();

        await using var node = await EngineNode.StartAsync(
            _server.ConnectionString, "fb-cyclic",
            o => o.CallTimeout = TimeSpan.FromSeconds(10));

        // Before the fix this call TIMED OUT: the response failed to encode, the exception
        // escaped past the ack, and the request went to lease recovery — where the handler,
        // which had already completed its business operation, would run again.
        var response = await node.Client.ExecuteAsync(new FbCyclicRequest());

        response.StatusCode.Should().Be(500);
        response.Error.Should().NotBeNull();
        response.Error!.Code.Should().Be("RESPONSE_SERIALIZATION_FAILED",
            "a fault in the ANSWER must not be reported as a fault in the work");

        // The heart of the defect: the handler must have run exactly once. Two entries here
        // means a completed, non-idempotent business operation was executed twice because its
        // response had a cycle in it.
        FbCyclicService.Invocations.Count.Should().Be(1);

        // And the request was acknowledged: give redelivery a moment to happen if it were
        // going to, then confirm it did not.
        await Task.Delay(500);
        FbCyclicService.Invocations.Count.Should().Be(1, "the request must be acked, not redelivered");
    }

    [Fact]
    public async Task ConstructorThrow_RepliesActivationError_InsteadOfTimingOut()
    {
        await using var node = await EngineNode.StartAsync(
            _server.ConnectionString, "fb-ctor",
            o => o.CallTimeout = TimeSpan.FromSeconds(10));

        // Before the fix this was a 10-second silence ending in a timeout, with the same
        // permanent error re-thrown on every redelivery. A missing DI registration is
        // knowable on the first attempt, and the caller should be told.
        var started = DateTime.UtcNow;
        var response = await node.Client.ExecuteAsync(new FbBadCtorRequest());
        var elapsed = DateTime.UtcNow - started;

        response.StatusCode.Should().Be(500);
        response.Error.Should().NotBeNull();
        response.Error!.Code.Should().Be("SERVICE_ACTIVATION_FAILED");
        response.Error.Message.Should().Contain(nameof(FbBadCtorService),
            "the operator's first question is WHICH class would not construct");

        elapsed.Should().BeLessThan(TimeSpan.FromSeconds(8),
            "an activation failure is deterministic and must not be reported by timeout");
    }
}

// =============================================================================
// Fixtures. Every engine in this assembly hosts these; both are inert unless
// actually called, so they cannot disturb other tests.
// =============================================================================

[Service("fb.cyclic")]
public sealed class FbCyclicRequest : IReturn<FbCyclicResponse>;

/// <summary>A response that defeats System.Text.Json: it contains itself.</summary>
public sealed class FbCyclicResponse : Output
{
    public FbCyclicResponse? Self { get; set; }
}

public sealed class FbCyclicService : AsyncService<FbCyclicRequest, FbCyclicResponse>
{
    public static readonly ConcurrentQueue<DateTime> Invocations = new();

    public override Task<FbCyclicResponse> ExecuteAsync(FbCyclicRequest request, CancellationToken ct = default)
    {
        // The business operation "succeeds" — that is the point. Only the answer is broken.
        Invocations.Enqueue(DateTime.UtcNow);

        var response = new FbCyclicResponse { StatusCode = StatusCodes.Status200OK };
        response.Self = response;
        return Task.FromResult(response);
    }
}

[Service("fb.badctor")]
public sealed class FbBadCtorRequest : IReturn<ItEchoResponse>;

/// <summary>Constructor always throws — the shape of a missing DI registration.</summary>
public sealed class FbBadCtorService : AsyncService<FbBadCtorRequest, ItEchoResponse>
{
    public FbBadCtorService()
        => throw new InvalidOperationException("Simulated missing dependency.");

    public override Task<ItEchoResponse> ExecuteAsync(FbBadCtorRequest request, CancellationToken ct = default)
        => Task.FromResult(new ItEchoResponse { StatusCode = StatusCodes.Status200OK });
}
