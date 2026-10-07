using FluentAssertions;
using Highway.Client.Engine;
using NSubstitute;
using StackExchange.Redis;
using Xunit;

namespace Highway.Client.Tests.Engine;

/// <summary>
/// 074 Part B: every <c>HW.PUBLISH</c> carries a publish id, created once per call so every retry of that call
/// reuses it — the broker then delivers a publish whose reply was lost only once.
/// </summary>
public class PublishIdTests
{
    private static string? IdOf(object[] args)
    {
        for (var i = 0; i + 1 < args.Length; i++)
            if (args[i] is "ID") return args[i + 1]?.ToString();
        return null;
    }

    [Fact]
    public async Task ARetriedPublish_ReusesItsPublishId()
    {
        var db = Substitute.For<IDatabase>();
        var calls = new List<object[]>();
        db.ExecuteAsync("HW.PUBLISH", Arg.Any<object[]>()).Returns(info =>
        {
            calls.Add((object[])info[1]);
            if (calls.Count == 1) throw new RedisServerException(HighwayConnection.TransientAbortMessage);   // retried
            return Task.FromResult(RedisResult.Create(2L));
        });
        var connection = new HighwayConnection(db);

        (await connection.PublishCommandAsync("news", [1, 2, 3])).Should().Be(2);

        calls.Should().HaveCount(2, "the transient failure was retried");
        IdOf(calls[0]).Should().NotBeNullOrEmpty();
        IdOf(calls[1]).Should().Be(IdOf(calls[0]), "a retry of one publish is the same publish");
    }

    [Fact]
    public async Task SeparatePublishes_GetSeparateIds()
    {
        var db = Substitute.For<IDatabase>();
        var calls = new List<object[]>();
        db.ExecuteAsync("HW.PUBLISH", Arg.Any<object[]>())
            .Returns(info => { calls.Add((object[])info[1]); return Task.FromResult(RedisResult.Create(1L)); });
        var connection = new HighwayConnection(db);

        await connection.PublishCommandAsync("news", [1]);
        await connection.PublishCommandAsync("news", [1]);

        IdOf(calls[0]).Should().NotBe(IdOf(calls[1]), "two calls are two publishes, even with the same payload");
    }

    [Fact]
    public async Task ADelayedPublish_SendsIdBeforeAt()
    {
        var db = Substitute.For<IDatabase>();
        object[]? sent = null;
        db.ExecuteAsync("HW.PUBLISH", Arg.Any<object[]>())
            .Returns(info => { sent = (object[])info[1]; return Task.FromResult(RedisResult.Create(1L)); });
        var connection = new HighwayConnection(db);

        await connection.PublishCommandAsync("news", [1], DateTimeOffset.UtcNow.AddMinutes(5));

        var words = sent!.Select(a => a?.ToString()).ToList();
        words.IndexOf("ID").Should().BeGreaterThan(0).And.BeLessThan(words.IndexOf("AT"));
    }
}
