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
                n.Services, n.Queues, n.Channels, n.SeenFrom)).ToArray();

            return Results.Json(new NodesDto(rows, null));
        });

        // One node's work (023 T6). This is a projection across EVERY entity rather than one,
        // which is why it did not come for free with the entity page: the recorder indexes by
        // entity name, and no index maps a node to the messages it handled. Building one would
        // be new storage for a view; scanning the bounded recorder is not.
        app.MapGet("/api/node/{name}", async (string name, IBrokerState state, FlightRecorder recorder) =>
        {
            var result = await state.NodesAsync();
            var node = result.Value?.FirstOrDefault(n => n.Name == name);

            var messages = new List<NodeMessageRowDto>();
            foreach (var entity in recorder.Names().Select(n => n.Name))
            {
                if (entity == "hw.replies") continue;

                foreach (var m in MessageProjection.Summarise(entity, ReadAllFor(recorder, entity)))
                {
                    // Attributed by where the work FINISHED, which is the only node these
                    // events name. The send side records none -- see 023's execution notes.
                    if (m.CompletedOnNode == name)
                        messages.Add(new NodeMessageRowDto(
                            entity, m.Id, m.Outcome.ToString(), m.CompletedAt, m.DurationMs, m.FailureDetail));
                }
            }

            messages.Sort((a, b) => Nullable.Compare(b.CompletedAt, a.CompletedAt));

            // A node the registry cannot confirm may still have visible work, so the page is
            // rendered either way and says which half is missing.
            return Results.Json(new NodeDetailDto(
                name,
                node is null ? "unknown" : node.IsLive ? "live"
                    : node.SinceLastSeen > TimeSpan.FromHours(1) ? "absent" : "stale",
                node?.SinceLastSeen.TotalSeconds ?? 0,
                node?.SeenFrom,
                node?.Services ?? [], node?.Queues ?? [], node?.Channels ?? [],
                messages,
                messages.Count(m => m.Outcome == "Processed"),
                messages.Count(m => m.Outcome is "Failed" or "DeadLettered"),
                node is null ? result.Unavailable ?? "this node is not in the registry" : null));
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

        // Messages, not protocol events (023). The correlation and the outcome are decided on
        // the server: a browser doing it would fetch every event to group them and re-implement
        // what "acknowledged" means.
        app.MapGet("/api/messages/{name}", (string name, FlightRecorder recorder) =>
        {
            var events = ReadAllFor(recorder, name, out var groups);
            var counts = MessageProjection.Count(name, events, groups);
            var messages = MessageProjection.Summarise(name, events, subscriberGroups: groups);

            return Results.Json(new MessageListDto(
                messages.Select(m => new MessageRowDto(
                    m.Id, m.Outcome.ToString(), m.StartedAt, m.StartedOnNode,
                    m.CompletedAt, m.CompletedOnNode, m.DurationMs, m.FailureDetail,
                    m.DeliveredGroups, m.SubscriberGroups)).ToArray(),
                counts.Processed, counts.Failed, counts.DeadLettered, counts.Refused,
                counts.InFlight, counts.WindowStart));
        });

        app.MapGet("/api/message/{name}/{id}", (string name, string id, FlightRecorder recorder) =>
        {
            var events = ReadAllFor(recorder, name)
                .Where(e => MessageProjection.KeyOf(e) == id)
                .ToArray();

            if (events.Length == 0)
                return Results.NotFound();

            var summary = MessageProjection.Summarise(name, events).FirstOrDefault();
            var steps = MessageProjection.Timeline(events);

            // The body obeys feature 002's capture modes. The dashboard is not an exemption
            // from the setting that exists to keep application data out of the recorder.
            var capture = GetCaptureMode(recorder, name);
            var payload = events.Select(e => e.Payload).FirstOrDefault(p => p is { Length: > 0 });

            var state = capture switch
            {
                PayloadCapture.Full when payload is not null => "captured",
                PayloadCapture.Full => "not-captured",
                PayloadCapture.HeadersOnly => "headers-only",
                _ => "disabled",
            };

            return Results.Json(new MessageDetailDto(
                id, name, summary?.Outcome.ToString() ?? "Incomplete",
                steps.Select(s => new MessageStepRowDto(
                    s.At, s.Type, s.Visibility.ToString(), s.Node, s.SincePreviousMs, s.Detail)).ToArray(),
                state == "captured" && payload is not null ? Convert.ToBase64String(payload) : null,
                state));
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

    /// <summary>
    /// Every retained event for a name, plus the reply bucket.
    ///
    /// <para>An RPC's reply is recorded under <c>hw.replies</c> (015's fix for one buffer per
    /// reply), so reconstructing a request-and-response needs both names. That is the one join
    /// in this feature which crosses recorder names.</para>
    /// </summary>
    private static IReadOnlyList<HighwayEvent> ReadAllFor(FlightRecorder recorder, string name)
        => ReadAllFor(recorder, name, out _);

    private static IReadOnlyList<HighwayEvent> ReadAllFor(
        FlightRecorder recorder, string name, out int subscriberGroups)
    {
        subscriberGroups = 0;
        if (!recorder.Enabled) return [];

        var events = recorder.Read(name, DateTimeOffset.MinValue, DateTimeOffset.MaxValue, null, int.MaxValue).ToList();

        // A PUBLISH finishes somewhere else. The channel records Published; the delivery and the
        // acknowledgement are recorded under each subscriber group, `{channel}@{node}`. Without
        // this join every message on a channel page sits at InFlight for ever -- which is what
        // it did on first contact, while the messages had in fact all been processed.
        //
        // Same rule as the reply join below, and the same reason: one message's life spans more
        // than one recorder name. Splitting on '@' is how `Catalogue.Classify` decides what a
        // group is, so this agrees with the catalogue rather than inventing a second rule.
        var groupPrefix = name + "@";
        foreach (var group in recorder.Names().Select(n => n.Name))
        {
            if (!group.StartsWith(groupPrefix, StringComparison.Ordinal)) continue;

            subscriberGroups++;
            events.AddRange(recorder.Read(group, DateTimeOffset.MinValue, DateTimeOffset.MaxValue, null, int.MaxValue));
        }

        var replies = recorder.Read("hw.replies", DateTimeOffset.MinValue, DateTimeOffset.MaxValue, null, int.MaxValue);
        var ids = events.Select(MessageProjection.KeyOf).Where(k => k is not null).ToHashSet();

        events.AddRange(replies.Where(r => ids.Contains(MessageProjection.KeyOf(r))));
        return events;
    }
}
