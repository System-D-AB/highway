namespace Highway.Abstractions;

/// <summary>
/// Marker interface for queued work — messages handled by exactly one
/// <see cref="IProcess{T}"/>, with no reply (feature 014).
///
/// <para>Named after the verb that sends it, matching <see cref="IPublish"/> and
/// <c>PublishAsync</c>.</para>
///
/// <para><b>Send or Publish?</b> One handler → <c>SendAsync</c>. Many handlers →
/// <c>PublishAsync</c>. Need the answer → <c>ExecuteAsync</c>.</para>
///
/// <para>The deployment consequence is the point of having both: three instances of a
/// processor <b>share</b> the work; three instances of a subscriber each get <b>their own
/// copy</b>.</para>
/// </summary>
public interface ISend;
