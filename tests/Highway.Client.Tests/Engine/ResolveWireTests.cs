using FluentAssertions;
using Highway.Abstractions;
using Highway.Client.Engine;
using NSubstitute;
using StackExchange.Redis;
using Xunit;

namespace Highway.Client.Tests.Engine;

/// <summary>073 verification: a handler's decision reaches the broker as decided — <c>Requeue(after)</c> included.</summary>
public class ResolveWireTests
{
    private static (HighwayConnection Connection, List<object[]> Sent) Connection()
    {
        var db = Substitute.For<IDatabase>();
        var sent = new List<object[]>();
        db.ExecuteAsync("HW.DLQ", Arg.Any<object[]>()).Returns(info =>
        {
            sent.Add((object[])info[1]);
            return Task.FromResult(RedisResult.Create([RedisResult.Create((RedisValue)"done"), RedisResult.Create((RedisValue)"requeued")]));
        });
        return (new HighwayConnection(db), sent);
    }

    [Fact]
    public async Task RequeueAfter_SendsAfterInMilliseconds()
    {
        var (connection, sent) = Connection();

        await connection.ResolveDeadLetterAsync("tok-1", DeadLetterAction.Requeue(TimeSpan.FromSeconds(2.5)));

        sent.Single().Select(a => a.ToString()).Should().Equal("RESOLVE", "tok-1", "REQUEUE", "AFTER", "2500");
    }

    [Fact]
    public async Task RequeueWithoutDelay_SendsNoAfter()
    {
        var (connection, sent) = Connection();

        await connection.ResolveDeadLetterAsync("tok-1", DeadLetterAction.Requeue());

        sent.Single().Select(a => a.ToString()).Should().Equal("RESOLVE", "tok-1", "REQUEUE");
    }
}
