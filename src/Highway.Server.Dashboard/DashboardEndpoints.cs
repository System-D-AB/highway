using Highway.Abstractions.Observability;
using Highway.Server.Observability;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Highway.Server.Dashboard;

internal static class DashboardEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/", (HttpContext ctx) =>
        {
            ctx.Response.ContentType = "text/html; charset=utf-8";
            return ctx.Response.WriteAsync(EmbeddedResources.GetIndex());
        });

        app.MapGet("/app.css", (HttpContext ctx) =>
        {
            ctx.Response.ContentType = "text/css; charset=utf-8";
            ctx.Response.Headers.CacheControl = "no-store";
            return ctx.Response.WriteAsync(EmbeddedResources.GetCss());
        });

        app.MapGet("/app.js", (HttpContext ctx) =>
        {
            ctx.Response.ContentType = "application/javascript; charset=utf-8";
            ctx.Response.Headers.CacheControl = "no-store";
            return ctx.Response.WriteAsync(EmbeddedResources.GetJs());
        });

        // Every module under wwwroot/js is served by one route: adding a view means adding a
        // file, not a route. No build step — the browser resolves the imports (022 R-5A).
        app.MapGet("/js/{module}.js", (string module, HttpContext ctx) =>
        {
            // Whitelisted by shape, not sanitised: a module name is an identifier, and anything
            // that is not one is not a module.
            if (!module.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
                return Results.NotFound();

            ctx.Response.ContentType = "application/javascript; charset=utf-8";
            ctx.Response.Headers.CacheControl = "no-store";
            return Results.Text(EmbeddedResources.GetModule(module), "application/javascript");
        });

        app.MapGet("/api/nodes", async (IBrokerState state) =>
        {
            var result = await state.NodesAsync();
            if (result.Value is null)
                return Results.Json(new NodesDto([], result.Unavailable));

            var rows = result.Value.Select(n => new NodeRowDto(
                n.Name,

                // Liveness is interpreted here, not in the browser. "Last seen 14:02:11" makes
                // an operator do arithmetic; "stale 4m" does not (022 R2.2).
                n.IsLive ? "live" : n.SinceLastSeen > TimeSpan.FromHours(1) ? "absent" : "stale",
                n.SinceLastSeen.TotalSeconds,
                n.Services, n.Queues, n.Channels)).ToArray();

            return Results.Json(new NodesDto(rows, null));
        });

        app.MapGet("/api/catalogue", async (IBrokerState state, FlightRecorder recorder) =>
        {
            // The recorder is in-process and needs no connection, so the observed half survives
            // even when the declared half cannot be read (022 R-3A, mTLS).
            var observed = recorder.Names().Select(n => n.Name).ToArray();

            var result = await state.CatalogueAsync(observed);
            var queues = await state.QueuesAsync();
            var byName = queues.Value?.ToDictionary(q => q.Name) ?? [];

            var rows = (result.Value ?? [])
                .Select(e =>
                {
                    byName.TryGetValue(e.Name, out var q);
                    return new CatalogueRowDto(
                        e.Name, e.Kind.ToString(), e.State.ToString(), e.ParentChannel, e.Hosts,
                        q?.Depth, q?.Bytes, q?.MaxBytes, q?.DeadLettered);
                })
                .ToArray();

            return Results.Json(new CatalogueDto(rows, result.Unavailable));
        });

        app.MapGet("/api/recorder", (FlightRecorder recorder, DashboardInfo info) =>
        {
            if (!recorder.Enabled)
            {
                return Results.Json(new RecorderDto(
                    Broker: info.BrokerEndpoint,
                    Enabled: false,
                    ReplayEnabled: false,
                    Names: [],
                    TotalEvents: 0,
                    TotalBytes: 0,
                    DroppedCapacity: 0,
                    DroppedBudget: 0,
                    Failures: 0,
                    ObserverFailures: 0));
            }

            var snapshot = recorder.Snapshot();
            var names = recorder.Names();

            return Results.Json(new RecorderDto(
                Broker: info.BrokerEndpoint,
                Enabled: snapshot.Enabled,
                ReplayEnabled: recorder.Options.ReplayEnabled,
                Names: names.Select(n => new NameDto(n.Name, n.Count, n.Bytes, n.Capture.ToString(), n.DroppedCapacity)).ToList(),
                TotalEvents: snapshot.Events,
                TotalBytes: snapshot.Bytes,
                DroppedCapacity: snapshot.DroppedCapacity,
                DroppedBudget: snapshot.DroppedBudget,
                Failures: snapshot.Failures,
                ObserverFailures: snapshot.ObserverFailures));
        });

        app.MapGet("/api/events/{name}", (string name, FlightRecorder recorder, HttpRequest req) =>
        {
            if (!recorder.Enabled)
                return Results.Json(new { events = Array.Empty<EventDto>(), state = "disabled" });

            // Check if name is known
            var knownNames = recorder.Names();
            var nameEntry = knownNames.FirstOrDefault(n => string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase));
            if (nameEntry.Name is null)
                return Results.Json(new { events = Array.Empty<EventDto>(), state = "unknown" });

            var from = ParseRelativeTime(req.Query["from"].FirstOrDefault()) ?? DateTimeOffset.UtcNow.AddMinutes(-5);
            var to = ParseRelativeTime(req.Query["to"].FirstOrDefault()) ?? DateTimeOffset.UtcNow;
            var node = req.Query["node"].FirstOrDefault();
            var limit = int.TryParse(req.Query["limit"].FirstOrDefault(), out var l)
                ? Math.Min(l, recorder.Options.ReplayMaxLimit)
                : recorder.Options.ReplayDefaultLimit;

            var events = recorder.Read(name, from, to, node, limit);
            var capture = GetCaptureMode(recorder, name);
            var replayEnabled = recorder.Options.ReplayEnabled;

            var projected = events.Select(e => EventProjection.Project(e, capture, replayEnabled)).ToList();

            return Results.Json(new { events = projected, state = "ok" });
        });

        app.MapGet("/api/stream/{name}", async (string name, FlightRecorder recorder, DashboardOptions options, StreamRegistry registry, HttpContext ctx) =>
        {
            if (!registry.TryAcquire())
            {
                ctx.Response.StatusCode = 503;
                await ctx.Response.WriteAsync($"Too many concurrent streams (limit: {options.MaxConcurrentStreams}).");
                return;
            }

            try
            {
                await using var stream = new EventStream(name, options.StreamBufferCapacity);
                recorder.Subscribe(stream);

                ctx.Response.ContentType = "text/event-stream";
                ctx.Response.Headers.CacheControl = "no-cache";
                ctx.Response.Headers["X-Accel-Buffering"] = "no";

                var capture = GetCaptureMode(recorder, name);
                var replayEnabled = recorder.Options.ReplayEnabled;
                var lastDropped = 0L;

                using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                    ctx.RequestAborted, registry.ShutdownToken);

                try
                {
                    await foreach (var evt in stream.Reader.ReadAllAsync(linked.Token))
                    {
                        var dto = EventProjection.Project(evt, capture, replayEnabled);
                        var json = System.Text.Json.JsonSerializer.Serialize(dto);
                        await ctx.Response.WriteAsync($"data: {json}\n\n", linked.Token);
                        await ctx.Response.Body.FlushAsync(linked.Token);

                        var currentDropped = stream.Dropped;
                        if (currentDropped > lastDropped)
                        {
                            await ctx.Response.WriteAsync($"event: dropped\ndata: {{\"count\":{currentDropped}}}\n\n", linked.Token);
                            await ctx.Response.Body.FlushAsync(linked.Token);
                            lastDropped = currentDropped;
                        }
                    }
                }
                catch (OperationCanceledException) { }
                finally
                {
                    recorder.Unsubscribe(stream);
                }
            }
            finally
            {
                registry.Release();
            }
        });
    }

    internal static PayloadCapture GetCaptureMode(FlightRecorder recorder, string name)
    {
        if (recorder.Options.Overrides.TryGetValue(name, out var over) && over?.Capture is not null)
            return over.Capture.Value;
        return recorder.Options.DefaultCapture;
    }

    private static DateTimeOffset? ParseRelativeTime(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        if (value.StartsWith('-') && value.Length > 1)
        {
            var unit = value[^1];
            if (int.TryParse(value[1..^1], out var amount))
            {
                return unit switch
                {
                    'm' => DateTimeOffset.UtcNow.AddMinutes(-amount),
                    'h' => DateTimeOffset.UtcNow.AddHours(-amount),
                    's' => DateTimeOffset.UtcNow.AddSeconds(-amount),
                    'd' => DateTimeOffset.UtcNow.AddDays(-amount),
                    _ => null
                };
            }
        }
        return DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;
    }
}
