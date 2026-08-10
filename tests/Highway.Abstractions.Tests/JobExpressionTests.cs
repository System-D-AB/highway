using FluentAssertions;
using Highway.Abstractions.Scheduling;
using Xunit;

namespace Highway.Abstractions.Tests;

/// <summary>
/// Feature 028 — the schedule expression engine. Shared by client validation and the
/// server's fire transaction, so its correctness is the feature's correctness: a wrong
/// <c>NextOccurrence</c> fires jobs at wrong times everywhere at once.
/// </summary>
public class JobExpressionTests
{
    private static readonly DateTime T = new(2026, 8, 10, 14, 30, 45, DateTimeKind.Utc);

    // ---- parsing ------------------------------------------------------------

    [Theory]
    [InlineData("daily:02:00")]
    [InlineData("daily:23:59")]
    [InlineData("every:1")]
    [InlineData("every:86400")]
    [InlineData("cron:0 3 * * SUN")]
    [InlineData("cron:*/15 * * * *")]
    [InlineData("cron:0 0 1 JAN MON-FRI")]
    public void ValidExpressions_Parse(string text)
        => JobExpression.TryParse(text, out _, out _).Should().BeTrue();

    [Theory]
    [InlineData("", "empty")]
    [InlineData("daily:25:00", "HH:mm")]
    [InlineData("daily:2am", "HH:mm")]
    [InlineData("every:0", "positive")]
    [InlineData("every:-5", "positive")]
    [InlineData("every:1.5", "positive")]
    [InlineData("cron:0 3 * *", "5 fields")]
    [InlineData("cron:61 * * * *", "range")]
    [InlineData("cron:* 25 * * *", "range")]
    [InlineData("cron:* * * * 9", "range")]
    [InlineData("cron:5-2 * * * *", "inverted")]
    [InlineData("hourly:5", "unknown")]
    public void InvalidExpressions_AreRejected_WithTheReason(string text, string reasonFragment)
    {
        JobExpression.TryParse(text, out _, out var error).Should().BeFalse();
        error.Should().ContainEquivalentOf(reasonFragment,
            "the rejection must teach the grammar (R1.7)");
    }

    // ---- daily --------------------------------------------------------------

    [Fact]
    public void Daily_BeforeTodaysTime_FiresToday()
        => JobExpression.Parse("daily:15:00").NextOccurrence(T)
            .Should().Be(new DateTime(2026, 8, 10, 15, 0, 0, DateTimeKind.Utc));

    [Fact]
    public void Daily_AfterTodaysTime_FiresTomorrow()
        => JobExpression.Parse("daily:02:00").NextOccurrence(T)
            .Should().Be(new DateTime(2026, 8, 11, 2, 0, 0, DateTimeKind.Utc));

    [Fact]
    public void Daily_ExactlyAtTheTime_FiresTomorrow_NeverNow()
        => JobExpression.Parse("daily:14:00")
            .NextOccurrence(new DateTime(2026, 8, 10, 14, 0, 0, DateTimeKind.Utc))
            .Should().Be(new DateTime(2026, 8, 11, 14, 0, 0, DateTimeKind.Utc),
                "'strictly after' is what makes re-arm monotonic — firing at the same instant twice is the loop");

    // ---- every --------------------------------------------------------------

    [Fact]
    public void Every_AnchorsToTheGivenInstant_WhichIsCatchUpOne()
        => JobExpression.Parse("every:900").NextOccurrence(T)
            .Should().Be(T.AddSeconds(900),
                "OD3: next is computed from NOW, collapsing any missed backlog");

    // ---- cron ---------------------------------------------------------------

    [Fact]
    public void Cron_SundayThreeAm()
        => JobExpression.Parse("cron:0 3 * * SUN").NextOccurrence(T)   // 2026-08-10 is a Monday
            .Should().Be(new DateTime(2026, 8, 16, 3, 0, 0, DateTimeKind.Utc));

    [Fact]
    public void Cron_EveryFifteenMinutes()
        => JobExpression.Parse("cron:*/15 * * * *").NextOccurrence(T)
            .Should().Be(new DateTime(2026, 8, 10, 14, 45, 0, DateTimeKind.Utc));

    [Fact]
    public void Cron_FirstOfMonth()
        => JobExpression.Parse("cron:0 0 1 * *").NextOccurrence(T)
            .Should().Be(new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc));

    [Fact]
    public void Cron_MonthName_AndYearRollover()
        => JobExpression.Parse("cron:30 6 15 JAN *").NextOccurrence(T)
            .Should().Be(new DateTime(2027, 1, 15, 6, 30, 0, DateTimeKind.Utc));

    [Fact]
    public void Cron_DomAndDow_BothRestricted_IsAUnion()
        // Standard cron: day-of-month 13 OR Friday, whichever comes first.
        => JobExpression.Parse("cron:0 12 13 * FRI").NextOccurrence(T)
            .Should().Be(new DateTime(2026, 8, 13, 12, 0, 0, DateTimeKind.Utc),
                "2026-08-13 is a Thursday and the 13th — dom matches before Friday the 14th");

    [Fact]
    public void Cron_Impossible_RefusesInsteadOfLooping()
    {
        var act = () => JobExpression.Parse("cron:0 0 30 FEB *").NextOccurrence(T);
        act.Should().Throw<InvalidOperationException>().WithMessage("*no occurrence*");
    }
}
