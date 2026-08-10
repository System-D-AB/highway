using Highway.Abstractions;
using Highway.Abstractions.Scheduling;

namespace Highway.Client;

/// <summary>
/// Recurring-job declarations (feature 028, OD1-B): schedules live at the composition root —
/// the deployable — never in the contract assembly. The contract's <c>[Queue]</c> names
/// <i>where</i>; these declarations name <i>when</i>.
///
/// <code>
/// services.AddHighway(o =>
/// {
///     o.Jobs.Daily&lt;GenerateStatements&gt;(new TimeOnly(2, 0));
///     o.Jobs.Every&lt;ReconcileLedger&gt;(TimeSpan.FromMinutes(15));
///     o.Jobs.Cron&lt;PruneAudit&gt;("0 3 * * SUN");
///     o.Jobs.Daily(new SyncRegion { Region = "EU" }, new TimeOnly(2, 0), name: "eu-sync");
/// });
/// </code>
///
/// <para><b>The template</b> (D8): the registered instance — <c>new T()</c> by default — is
/// serialized once at engine start and replayed by the broker on every fire. Occurrences
/// carry identical bytes; per-occurrence data is derived by the handler from state.</para>
/// </summary>
public sealed class JobsOptions
{
    internal sealed record Registration(Type ContractType, string JobName, string Expression, object Template);

    internal List<Registration> Registrations { get; } = [];

    /// <summary>Fires every day at <paramref name="atUtc"/> (UTC — 028 is UTC-only, OD2).</summary>
    public JobsOptions Daily<T>(TimeOnly atUtc, string? name = null) where T : ISend, new()
        => Daily(new T(), atUtc, name);

    /// <summary>Daily, with an explicit template instance (fixed per-schedule configuration, D8).</summary>
    public JobsOptions Daily<T>(T template, TimeOnly atUtc, string? name = null) where T : ISend
        => Add(template!, $"daily:{atUtc:HH\\:mm}", name);

    /// <summary>
    /// Fires every <paramref name="interval"/>, anchored to the previous fire. Floor: one
    /// minute — below that a queue and a loop is the right tool, and the floor says so.
    /// (The wire accepts down to one second, for tests and tooling.)
    /// </summary>
    public JobsOptions Every<T>(TimeSpan interval, string? name = null) where T : ISend, new()
        => Every(new T(), interval, name);

    /// <summary>Interval, with an explicit template instance.</summary>
    public JobsOptions Every<T>(T template, TimeSpan interval, string? name = null) where T : ISend
    {
        if (interval < TimeSpan.FromMinutes(1))
            throw new InvalidOperationException(
                $"Jobs.Every interval must be at least 1 minute, but was {interval}. " +
                "For sub-minute work, use a queue and a loop — a scheduler is the wrong tool.");

        return Add(template!, $"every:{(long)interval.TotalSeconds}", name);
    }

    /// <summary>Standard 5-field cron (minute hour day-of-month month day-of-week), UTC.</summary>
    public JobsOptions Cron<T>(string expression, string? name = null) where T : ISend, new()
        => Cron(new T(), expression, name);

    /// <summary>Cron, with an explicit template instance.</summary>
    public JobsOptions Cron<T>(T template, string expression, string? name = null) where T : ISend
        => Add(template!, $"cron:{expression}", name);

    private JobsOptions Add(object template, string expression, string? name)
    {
        // Fail-fast (005 R12): a bad expression stops startup with the grammar in the message.
        if (!JobExpression.TryParse(expression, out _, out var reason))
            throw new InvalidOperationException(
                $"Job schedule '{expression}' for {template.GetType().Name} is invalid: {reason}. " +
                $"Accepted: {JobExpression.AcceptedForms}.");

        var jobName = name ?? DefaultName(template.GetType());
        HighwayOptionsValidator.ValidateIdentifier(jobName, "Jobs (job name)");

        if (Registrations.Any(r => r.JobName == jobName && r.ContractType == template.GetType()))
            throw new InvalidOperationException(
                $"A job named '{jobName}' is already declared for {template.GetType().Name}. " +
                "Give the second schedule an explicit name: o.Jobs.Daily(..., name: \"eu-sync\").");

        Registrations.Add(new Registration(template.GetType(), jobName, expression, template));
        return this;
    }

    /// <summary>Default job name: the contract's type name, kebab-free and lowercase.</summary>
    private static string DefaultName(Type contract) => contract.Name.ToLowerInvariant();
}
