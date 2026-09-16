using FluentAssertions;
using Highway.Server.Resp;
using Xunit;

namespace Highway.Server.Tests.Resp;

/// <summary>
/// Feature 048 — the node-name → observed-address map behind the dashboard "Seen from" column.
/// </summary>
public class ObservedAddressRegistryTests
{
    [Fact]
    public void Record_ThenAddressOf_ReturnsTheEndpoint()
    {
        var reg = new ObservedAddressRegistry();
        reg.Record("conn-1", "nodeA", "10.0.0.9:6500");

        reg.AddressOf("nodeA").Should().Be("10.0.0.9:6500");
        reg.AddressOf("unknown").Should().BeNull("a node with no named connection is 'not connected'");
    }

    [Fact]
    public void Remove_ClearsTheEntry()
    {
        var reg = new ObservedAddressRegistry();
        reg.Record("conn-1", "nodeA", "10.0.0.9:6500");

        reg.Remove("conn-1");

        reg.AddressOf("nodeA").Should().BeNull("the observed address is gone with the connection");
    }

    [Fact]
    public void EmptyInputs_AreIgnored()
    {
        var reg = new ObservedAddressRegistry();
        reg.Record("", "nodeA", "x");
        reg.Record("c", "", "x");
        reg.Record("c", "nodeA", "");

        reg.AddressOf("nodeA").Should().BeNull();
    }
}
