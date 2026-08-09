using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Highway.Client.Engine;

/// <summary>Which family a failed message belongs to — the grammar <c>HW.DLQ</c> already parses.</summary>
internal enum FailureFamily
{
    /// <summary>An RPC request in a service's processing list.</summary>
    Service,

    /// <summary>A queued message in a queue's processing list (feature 014).</summary>
    Queue,

    /// <summary>A pub/sub message in a channel group's processing list.</summary>
    Channel,
}

/// <summary>
/// Identifies the message that failed: the family, the service/queue/channel name, and the
/// scope within it — a node for services and queues, a group for channels.
/// </summary>
internal readonly record struct FailureTarget(FailureFamily Family, string Name, string Scope)
{
    /// <summary>The noun used in log messages: "service", "queue" or "channel".</summary>
    public string Kind => Family switch
    {
        FailureFamily.Service => "service",
        FailureFamily.Queue => "queue",
        _ => "channel",
    };

    /// <summary>The wire token <c>HW.FAIL</c> expects: <c>SVC</c> or <c>Q</c>.</summary>
    public string WireKind => Family switch
    {
        FailureFamily.Service => "SVC",
        _ => "Q",
    };
}

/// <summary>
/// The one place a handler exception is reported from. All worker loops route through it —
/// <see cref="SingleMessageWorkerLoop"/> for services, queues and subscribers — because failure
/// reporting is the concern they genuinely have in common.
///
/// <para><b>Reporting never breaks delivery.</b> A failed <c>HW.FAIL</c> is swallowed and
/// logged <i>with the original exception attached</i>, the loop continues, and the message is
/// still not acknowledged — so the lease sweep recovers it exactly as before, just without
/// context. This is the same rule feature 002 states for the flight recorder: a mechanism that
/// observes the system must never be able to break it.</para>
///
/// <para><b>Bounding happens here, before the wire.</b> A stack trace can be tens of kilobytes
/// and the server would only discard the excess, so bytes that will be thrown away are never
/// transmitted. The <i>capture mode</i> is applied server-side instead, because it is a
/// per-name server setting (feature 002) and a client copy would be a second thing to get
/// wrong.</para>
/// </summary>
internal sealed class FailureReporter(IHighwayConnection connection, ILogger logger)
{
    /// <summary>
    /// Maximum bytes kept for the exception message and for the stack trace, each.
    ///
    /// <para>Generous enough that a real stack survives intact — the top frames are the useful
    /// ones and they come first — and small enough that a pathological exception cannot push a
    /// megabyte into a dead letter that an operator then has to page through.</para>
    /// </summary>
    public const int MaxMessageChars = 2_000;

    /// <inheritdoc cref="MaxMessageChars"/>
    public const int MaxStackChars = 8_000;

    /// <summary>Appended to anything cut, so a truncated field never reads as a complete one.</summary>
    public const string TruncationMarker = "… [truncated]";

    /// <summary>
    /// Reports that a handler threw. <paramref name="disposition"/> states what happens to the
    /// message as a result — the three loops answer that differently, and the difference is the
    /// part an operator reading the log actually needs.
    /// </summary>
    public async Task ReportAsync(
        FailureTarget target,
        string messageId,
        Exception exception,
        string disposition,
        CancellationToken cancellationToken = default)
    {
        logger.LogError(exception,
            "Handler failed for message '{MessageId}' on {Kind} '{Target}'; {Disposition}",
            messageId, target.Kind, target.Name, disposition);

        try
        {
            await connection.FailAsync(
                target.WireKind, target.Name, target.Scope, messageId,
                exception.GetType().FullName ?? exception.GetType().Name,
                BuildDetail(exception, target.Scope),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception reportingFailure)
        {
            // Swallowed deliberately, and logged WITH the original attached: an operator who
            // loses the diagnosis must at least not also lose the thing being diagnosed. The
            // message is still unacknowledged, so the sweep recovers it either way.
            logger.LogWarning(
                new AggregateException(reportingFailure, exception),
                "Could not report the failure of '{MessageId}' on {Kind} '{Target}'; the message is " +
                "unaffected and will still be recovered, but its dead letter will not say why it died",
                messageId, target.Kind, target.Name);
        }
    }

    /// <summary>
    /// The detail blob. Opaque to the server, which stores it verbatim and never parses it.
    /// </summary>
    internal static byte[] BuildDetail(Exception exception, string node)
    {
        var buffer = new MemoryStream(512);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("message", Truncate(exception.Message, MaxMessageChars));
            writer.WriteString("node", node);
            writer.WriteString("at", DateTimeOffset.UtcNow.ToString("O"));

            if (exception.StackTrace is { Length: > 0 } stack)
                writer.WriteString("stack", Truncate(stack, MaxStackChars));

            // The inner exception's type alone, not its whole chain. "TimeoutException wrapping
            // a SocketException" is the sentence an operator needs; the full chain is what the
            // application's own logging is for.
            if (exception.InnerException is { } inner)
                writer.WriteString("inner", inner.GetType().FullName ?? inner.GetType().Name);

            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// Keeps the first <paramref name="max"/> characters and marks the cut. The <i>front</i> of
    /// a stack trace is the part that says where it threw, so truncating the tail keeps the
    /// useful half.
    /// </summary>
    internal static string Truncate(string value, int max)
        => value.Length <= max ? value : string.Concat(value.AsSpan(0, max), TruncationMarker);
}
