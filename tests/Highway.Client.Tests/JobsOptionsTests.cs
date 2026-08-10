using FluentAssertions;
using Highway.Abstractions;
using Highway.Client;
using Xunit;

namespace Highway.Client.Tests;

/// <summary>
/// Feature 028 T4 — the composition-root declaration API (OD1-B). Every misdeclaration fails
/// at startup with a message that teaches; nothing waits for the broker to find out.
/// </summary>
public class JobsOptionsTests
{
    [Queue("jt.work")]
    public sealed record JtWork : ISend
    {
        public string? Region { get; init; }
    }

    [Fact]
    public void Daily_Every_Cron_AllRegister()
    {
        var jobs = new JobsOptions()
            .Daily<JtWork>(new TimeOnly(2, 0))
            .Every<JtWork>(TimeSpan.FromMinutes(15), name: "quarter-hourly")
            .Cron<JtWork>("0 3 * * SUN", name: "weekly");

        jobs.Registrations.Should().HaveCount(3);
        jobs.Registrations.Select(r => r.Expression)
            .Should().Equal("daily:02:00", "every:900", "cron:0 3 * * SUN");
    }

    [Fact]
    public void TemplateInstance_IsCarried_NotReconstructed()
    {
        var jobs = new JobsOptions()
            .Daily(new JtWork { Region = "EU" }, new TimeOnly(2, 0), name: "eu")
            .Daily(new JtWork { Region = "US" }, new TimeOnly(6, 0), name: "us");

        jobs.Registrations.Select(r => ((JtWork)r.Template).Region)
            .Should().Equal("EU", "US");
    }

    [Fact]
    public void Every_BelowOneMinute_IsRefused_NamingTheRightTool()
    {
        var act = () => new JobsOptions().Every<JtWork>(TimeSpan.FromSeconds(30));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*at least 1 minute*").WithMessage("*queue and a loop*");
    }

    [Fact]
    public void InvalidCron_FailsAtDeclaration_TeachingTheGrammar()
    {
        var act = () => new JobsOptions().Cron<JtWork>("0 3 * *");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*5 fields*").WithMessage("*daily:HH:mm*");
    }

    [Fact]
    public void DuplicateJobName_IsRefused_SuggestingAnExplicitName()
    {
        var jobs = new JobsOptions().Daily<JtWork>(new TimeOnly(2, 0));

        var act = () => jobs.Daily<JtWork>(new TimeOnly(6, 0));

        act.Should().Throw<InvalidOperationException>().WithMessage("*explicit name*");
    }

    [Fact]
    public void JobName_ObeysIdentifierRules()
    {
        var act = () => new JobsOptions().Daily<JtWork>(new TimeOnly(2, 0), name: "bad@name");

        act.Should().Throw<InvalidOperationException>().WithMessage("*'@'*");
    }
}
