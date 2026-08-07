namespace Highway.Abstractions.Observability;

/// <summary>
/// How much of a payload the flight recorder retains, configured globally and
/// overridable per service or channel name.
/// </summary>
public enum PayloadCapture
{
    /// <summary>
    /// Retain the full payload bytes. <b>The default.</b>
    ///
    /// <para><b>Consequence, stated plainly:</b> payload content sits in server
    /// memory and is readable by anyone who can issue <c>HW.REPLAY</c>, and
    /// Highway has no authentication. For names carrying personal or sensitive
    /// data use <see cref="HeadersOnly"/>, or disable replay entirely while
    /// keeping the recorder for metrics.</para>
    /// </summary>
    Full = 0,

    /// <summary>
    /// Retain metadata only — name, node, identifiers, timestamp, outcome, and
    /// the payload's <em>size</em> — but no payload content. The size is still
    /// reported, so throughput and shape remain visible without the data.
    /// </summary>
    HeadersOnly = 1,

    /// <summary>
    /// Record nothing for this name. No buffer is allocated, so the cost of a
    /// disabled name is a dictionary miss.
    /// </summary>
    Off = 2,
}
