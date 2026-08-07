using System.Diagnostics;

namespace Highway.Client.Observability;

/// <summary>
/// Client-side distributed-tracing spans (feature 002).
///
/// <para><b>No OpenTelemetry dependency.</b> Highway emits
/// <see cref="Activity"/> from a named <see cref="ActivitySource"/> — in-box,
/// zero packages — and the hosting application wires OpenTelemetry and
/// subscribes if it wants OTLP. That is how <c>HttpClient</c> and ASP.NET Core
/// do it, and it matters here for three reasons: <c>Highway.Client</c> stays
/// light for every consuming application; the application keeps control of
/// sampling, exporters and resource attributes rather than inheriting Highway's;
/// and Highway takes on no telemetry-stack version conflicts.</para>
///
/// <para>With no listener attached, <see cref="ActivitySource.StartActivity(string, ActivityKind)"/>
/// returns null and nothing is materialised, so emission is essentially free
/// when unobserved.</para>
/// </summary>
internal static class HighwayActivity
{
    /// <summary>Source name applications subscribe to. Part of the documented protocol surface.</summary>
    public const string SourceName = "Highway.Client";

    private static readonly ActivitySource Source = new(SourceName);

    /// <summary>True when something is collecting — lets callers skip building arguments.</summary>
    public static bool Enabled => Source.HasListeners();

    /// <summary>Starts a caller-side span for an RPC call.</summary>
    public static Activity? StartCall(string service, string requestId, string nodeName)
    {
        var activity = Source.StartActivity($"highway.call {service}", ActivityKind.Client);
        if (activity is null) return null;

        activity.SetTag("messaging.system", "highway");
        activity.SetTag("messaging.operation", "process");
        activity.SetTag("messaging.destination.name", service);
        activity.SetTag("messaging.message.id", requestId);
        activity.SetTag("messaging.client.id", nodeName);
        return activity;
    }

    /// <summary>Starts a caller-side span for a publish.</summary>
    public static Activity? StartPublish(string channel, string nodeName)
    {
        var activity = Source.StartActivity($"highway.publish {channel}", ActivityKind.Producer);
        if (activity is null) return null;

        activity.SetTag("messaging.system", "highway");
        activity.SetTag("messaging.operation", "publish");
        activity.SetTag("messaging.destination.name", channel);
        activity.SetTag("messaging.client.id", nodeName);
        return activity;
    }

    /// <summary>
    /// The current W3C traceparent, or null when nothing is being traced.
    /// Rides the envelope's optional <c>tp</c> field so a server-side span can
    /// join the caller's trace.
    /// </summary>
    public static string? CurrentTraceParent()
        => Activity.Current is { IdFormat: ActivityIdFormat.W3C } current ? current.Id : null;

    /// <summary>Records the outcome on the span, if one is active.</summary>
    public static void SetOutcome(Activity? activity, int? statusCode, string? errorCode = null)
    {
        if (activity is null) return;

        if (statusCode is { } status)
        {
            activity.SetTag("highway.status_code", status);
            activity.SetStatus(status < 400 ? ActivityStatusCode.Ok : ActivityStatusCode.Error);
        }

        if (errorCode is not null)
        {
            activity.SetTag("highway.error_code", errorCode);
            activity.SetStatus(ActivityStatusCode.Error);
        }
    }
}
