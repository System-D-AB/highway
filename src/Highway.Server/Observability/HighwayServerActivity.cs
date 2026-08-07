using System.Diagnostics;

namespace Highway.Server.Observability;

/// <summary>
/// Server-side distributed-tracing spans (feature 002).
///
/// <para>Emits <see cref="Activity"/> from a named source. No OpenTelemetry
/// dependency — see <c>docs/HIGHWAY-PROTOCOL.md</c> § "Activity emission" for
/// the reasoning and for how an application subscribes.</para>
/// </summary>
internal static class HighwayServerActivity
{
    /// <summary>Source name applications subscribe to. Part of the documented protocol surface.</summary>
    public const string SourceName = "Highway.Server";

    private static readonly ActivitySource Source = new(SourceName);

    /// <summary>True when something is collecting.</summary>
    public static bool Enabled => Source.HasListeners();

    /// <summary>
    /// Starts a server-side span, joining the caller's trace when the envelope
    /// carried a <c>tp</c> field.
    /// </summary>
    public static Activity? Start(string operation, string name, string? traceParent)
    {
        if (!Source.HasListeners())
            return null;

        var parent = default(ActivityContext);
        if (traceParent is not null)
            ActivityContext.TryParse(traceParent, null, out parent);

        var activity = Source.StartActivity($"highway.{operation} {name}", ActivityKind.Consumer, parent);
        if (activity is null) return null;

        activity.SetTag("messaging.system", "highway");
        activity.SetTag("messaging.operation", operation);
        activity.SetTag("messaging.destination.name", name);
        return activity;
    }
}
