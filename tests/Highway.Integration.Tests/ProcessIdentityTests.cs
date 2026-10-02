using FluentAssertions;
using Highway.Abstractions.Observability;
using Highway.Server;
using Highway.Server.Observability;
using Xunit;

namespace Highway.Integration.Tests;

/// <summary>
/// Feature 072 end to end: real engines against a real broker. Every connection carries the process's
/// instance id, the broker counts the processes behind each node name, and says so when there is more
/// than one — the shape of three copies of one web application started with one explicit name.
/// </summary>
public class ProcessIdentityTests : IAsyncLifetime
{
    private readonly HighwayTestServer _server = new();
    private readonly List<EngineNode> _nodes = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var node in _nodes) await node.DisposeAsync();
        _server.Dispose();
    }

    private async Task<EngineNode> Start(string name)
    {
        var node = await EngineNode.StartAsync(_server.ConnectionString, name);
        _nodes.Add(node);
        return node;
    }

    private async Task<NodeDto> WaitForInstances(string name, int count)
    {
        NodeDto? node = null;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            node = (await _server.ReadNodesAsync()).FirstOrDefault(n => n.Name == name);
            if (node?.Instances?.Count == count && node.Instances.All(i => i.Host is not null)) return node;
            await Task.Delay(50);
        }
        node.Should().NotBeNull();
        node!.Instances.Should().HaveCount(count, "the broker should see {0} process(es) behind '{1}'", count, name);
        return node;
    }

    private List<HighwayEvent> SharedEvents(string name)
        => [.. _server.Recorder.Read(name, DateTimeOffset.MinValue, DateTimeOffset.MaxValue, null, 100)
            .Where(e => e.EventType == HighwayEventType.NodeNameShared)];

    [Fact]
    public async Task OneEngine_IsOneProcess_WithWhatItSaidAboutItself()
    {
        await Start("id-solo");

        var node = await WaitForInstances("id-solo", 1);

        node.Shared.Should().BeFalse();
        var instance = node.Instances!.Single();
        instance.Id.Should().MatchRegex("^[0-9a-f]{12}$", "one engine's connections all carry its one instance id");
        instance.Host.Should().Be(Environment.MachineName);
        instance.Pid.Should().Be(Environment.ProcessId);
        instance.SeenFrom.Should().NotBeNullOrEmpty();
        SharedEvents("id-solo").Should().BeEmpty();
    }

    [Fact]
    public async Task TwoEngines_OneName_AreTwoProcesses_AndTheBrokerSaysSo()
    {
        await Start("id-shared");
        var second = await Start("id-shared");

        var node = await WaitForInstances("id-shared", 2);

        node.Shared.Should().BeTrue();
        node.Instances!.Select(i => i.Id).Should().OnlyHaveUniqueItems();
        SharedEvents("id-shared").Should().ContainSingle().Which.Count.Should().Be(2);

        await second.DisposeAsync();
        _nodes.Remove(second);

        (await WaitForInstances("id-shared", 1)).Shared.Should().BeFalse();
        SharedEvents("id-shared").Should().Contain(e => e.Count == 1, "back to one process is recorded too");
    }

    [Fact]
    public async Task ANameTheConnectionSanitises_StillShowsItsProcess()
    {
        // 072 review, finding 1: the client names the connection with every character outside
        // [A-Za-z0-9-_.] as '_'; the broker must match on that same form, or a node named after a Windows
        // service like MSSQL$X (or a non-ASCII machine) shows "not connected" and is never flagged.
        const string name = "id-app-WEB1-MSSQL$X ä";
        await Start(name);

        var node = await WaitForInstances(name, 1);

        node.SeenFrom.Should().NotBeNull();
        node.Instances!.Single().Pid.Should().Be(Environment.ProcessId);
    }

    [Fact]
    public async Task TwoEngines_DistinctNames_AreTwoNodes()
    {
        await Start("id-shop1");
        await Start("id-shop2");

        (await WaitForInstances("id-shop1", 1)).Shared.Should().BeFalse();
        (await WaitForInstances("id-shop2", 1)).Shared.Should().BeFalse();
    }
}
