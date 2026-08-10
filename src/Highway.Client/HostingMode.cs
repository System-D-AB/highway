namespace Highway.Client;

/// <summary>
/// Decides which assemblies may contribute <b>handlers</b> — services, processors,
/// subscribers — to this process (feature 024).
///
/// <para><b>Contract discovery is unaffected by this setting.</b> Contracts are found across
/// the full reference closure in every mode; a caller-only process keeps finding every route
/// it references. The mode governs only what this process <i>hosts</i>.</para>
///
/// <para>The problem the non-default modes solve: under <see cref="Implicit"/>, referencing a
/// library for one helper class hosts every handler that library contains — which application
/// processes a queue becomes a property of the dependency graph. Three independent
/// architecture reviews found this same gap (see
/// <c>docs/features/024-hosting-boundaries/requirements.md</c>).</para>
/// </summary>
public enum HostingMode
{
    /// <summary>
    /// Handlers from every scanned assembly — the original behavior, and the default.
    /// A handler contributed by an assembly other than the entry assembly or a declared
    /// module is hosted <i>and logged as a warning</i> at startup, so the
    /// reference-equals-hosting accident announces itself.
    /// </summary>
    Implicit = 0,

    /// <summary>
    /// Handlers from the entry assembly plus declared modules
    /// (<c>[assembly: HighwayHostModule]</c> or <c>HighwayOptions.HostAssembly</c>) only.
    /// Handlers found elsewhere are skipped and reported.
    ///
    /// <para><b>Test-host caveat:</b> under a unit-test runner the entry assembly is the
    /// runner (<c>testhost</c>), not the test project — a test using this mode must declare
    /// its fixture assembly explicitly.</para>
    /// </summary>
    Declared = 1,

    /// <summary>
    /// Handlers from declared modules only; even the entry assembly hosts nothing unless
    /// declared. For teams that want every hosting decision visible at the composition root.
    /// </summary>
    ExplicitOnly = 2,
}
