using Highway.Abstractions.Observability;

namespace Highway.Server.Observability;

/// <summary>Per-name overrides for the flight recorder.</summary>
public sealed class NameRecorderOptions
{
    /// <summary>Events retained for this name. <c>null</c> uses the global default.</summary>
    public int? Capacity { get; set; }

    /// <summary>How long events remain visible. <c>null</c> uses the global default.</summary>
    public TimeSpan? Retention { get; set; }

    /// <summary>Payload capture for this name. <c>null</c> uses the global default.</summary>
    public PayloadCapture? Capture { get; set; }
}

/// <summary>
/// Flight recorder and activity-emission settings (feature 002).
///
/// <para>The defaults produce a useful recorder with no configuration. Every
/// non-obvious default carries its rationale here rather than in a separate
/// document, because the rationale is what stops someone "simplifying" it.</para>
/// </summary>
public sealed class ObservabilityOptions
{
    /// <summary>
    /// Master switch for the flight recorder. When <c>false</c> no buffers are
    /// allocated and recording costs a single branch. Default: <c>true</c>.
    /// </summary>
    public bool RecorderEnabled { get; set; } = true;

    /// <summary>
    /// Events retained per name before the oldest is dropped. Per-name rather
    /// than global, so a high-volume name cannot evict a quiet one's history.
    /// Default: 1,000.
    /// </summary>
    public int DefaultCapacity { get; set; } = 1_000;

    /// <summary>
    /// How long an event remains visible to <c>HW.REPLAY</c>. Enforced at read
    /// as well as by the sweep, so a stale event is never returned merely
    /// because the sweeper has not run. Default: 1 hour.
    ///
    /// <para>An hour rather than a day because the recorder is a debugging aid
    /// for what just happened, and its memory is better spent on depth than on
    /// age. Anything needing longer belongs in the OpenTelemetry pipeline.</para>
    /// </summary>
    public TimeSpan DefaultRetention { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Payload capture for names without an override. Default:
    /// <see cref="PayloadCapture.Full"/> — see that value's documentation for
    /// the data-exposure consequence.
    /// </summary>
    public PayloadCapture DefaultCapture { get; set; } = PayloadCapture.Full;

    /// <summary>
    /// Approximate ceiling on total recorder memory across all names. When
    /// exceeded, the sweep trims the largest buffers first. Default: 64 MiB.
    ///
    /// <para>Modest on purpose. The recorder shares the process with Garnet's
    /// store, and a debugging aid should not compete with the queues it exists
    /// to explain. Raise it deliberately, not by default.</para>
    /// </summary>
    public long MaxBytes { get; set; } = 64L * 1024 * 1024;

    /// <summary>How often the sweeper reclaims expired events and enforces the budget. Default: 10 seconds.</summary>
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Whether <c>HW.REPLAY</c> serves queries. Setting this <c>false</c> keeps
    /// the recorder and its metrics while refusing to hand out payloads — the
    /// switch for operators who want the counters but not the exposure.
    /// Default: <c>true</c>.
    /// </summary>
    public bool ReplayEnabled { get; set; } = true;

    /// <summary>Default number of events <c>HW.REPLAY</c> returns when no LIMIT is given. Default: 100.</summary>
    public int ReplayDefaultLimit { get; set; } = 100;

    /// <summary>Maximum accepted <c>HW.REPLAY</c> LIMIT. Above this is rejected. Default: 1,000.</summary>
    public int ReplayMaxLimit { get; set; } = 1_000;

    /// <summary>How far back <c>HW.REPLAY</c> looks when no FROM is given. Default: 5 minutes.</summary>
    public TimeSpan ReplayDefaultWindow { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Whether the server emits <see cref="System.Diagnostics.Activity"/> spans.
    /// Independent of the recorder. Default: <c>true</c> — emission costs
    /// nothing when no listener is attached.
    /// </summary>
    public bool ActivitiesEnabled { get; set; } = true;

    /// <summary>Per-name overrides. A name set to <see cref="PayloadCapture.Off"/> or zero capacity is never recorded.</summary>
    public Dictionary<string, NameRecorderOptions> Overrides { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Validates the configuration, naming the offending value.</summary>
    public void Validate()
    {
        if (DefaultCapacity < 0)
            throw new InvalidOperationException(
                $"ObservabilityOptions.DefaultCapacity cannot be negative, but was {DefaultCapacity}.");

        if (DefaultRetention < TimeSpan.Zero)
            throw new InvalidOperationException(
                $"ObservabilityOptions.DefaultRetention cannot be negative, but was {DefaultRetention}.");

        if (MaxBytes <= 0)
            throw new InvalidOperationException(
                $"ObservabilityOptions.MaxBytes must be positive, but was {MaxBytes}.");

        if (SweepInterval <= TimeSpan.Zero)
            throw new InvalidOperationException(
                $"ObservabilityOptions.SweepInterval must be positive, but was {SweepInterval}.");

        if (ReplayDefaultLimit < 1)
            throw new InvalidOperationException(
                $"ObservabilityOptions.ReplayDefaultLimit must be at least 1, but was {ReplayDefaultLimit}.");

        if (ReplayMaxLimit < ReplayDefaultLimit)
            throw new InvalidOperationException(
                $"ObservabilityOptions.ReplayMaxLimit ({ReplayMaxLimit}) cannot be below " +
                $"ReplayDefaultLimit ({ReplayDefaultLimit}).");

        if (ReplayDefaultWindow <= TimeSpan.Zero)
            throw new InvalidOperationException(
                $"ObservabilityOptions.ReplayDefaultWindow must be positive, but was {ReplayDefaultWindow}.");

        foreach (var (name, over) in Overrides)
        {
            if (over.Capacity is < 0)
                throw new InvalidOperationException(
                    $"ObservabilityOptions.Overrides['{name}'].Capacity cannot be negative, but was {over.Capacity}.");

            if (over.Retention is { } r && r < TimeSpan.Zero)
                throw new InvalidOperationException(
                    $"ObservabilityOptions.Overrides['{name}'].Retention cannot be negative, but was {r}.");
        }
    }
}
