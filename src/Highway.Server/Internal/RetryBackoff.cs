namespace Highway.Server.Internal;

/// <summary>
/// The retry-backoff schedule (feature 013).
///
/// <para>Roughly exponential, capped. The <b>cap</b> is the load-bearing part: an uncapped
/// exponential reaches hours by the twelfth attempt, at which point the message is
/// functionally dead but is still occupying a live queue and still counting toward
/// nothing. The curve below simply gets out of the way of a transient failure without
/// hammering a consumer that is failing for a structural reason.</para>
/// </summary>
internal static class RetryBackoff
{
    private static readonly TimeSpan[] Schedule =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(30),
    ];

    /// <summary>
    /// Delay before an entry on its <paramref name="attempt"/>-th delivery becomes
    /// claimable again, never exceeding <paramref name="cap"/>.
    /// </summary>
    public static TimeSpan For(int attempt, TimeSpan cap)
    {
        if (attempt <= 0) return TimeSpan.Zero;

        var delay = attempt <= Schedule.Length
            ? Schedule[attempt - 1]
            : cap;

        return delay > cap ? cap : delay;
    }
}
