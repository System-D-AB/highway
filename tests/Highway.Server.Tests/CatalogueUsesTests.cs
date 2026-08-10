using System.Text;
using FluentAssertions;
using Highway.Server.Internal;
using Highway.Server.Observability;
using Xunit;

namespace Highway.Server.Tests;

/// <summary>
/// Feature 024 T6 — the can-use half of a node's catalog reaches the registry additively.
///
/// <para>The compatibility rule under test is R4.1: the catalog is JSON, so a pre-024 record
/// without <c>uses</c> must read as empty — never as an error, and never by misparsing. The
/// asymmetry with the node-address decision (023 T8, which chose <i>not</i> to store) is
/// deliberate: <c>uses</c> is derived from the node's own scan and changes only on redeploy,
/// so the registration it already sends is the right vehicle.</para>
/// </summary>
public class CatalogueUsesTests
{
    private static NodeDto Read(string catalogJson)
    {
        var record = NodeRegistration.Encode(
            DateTime.UtcNow.Ticks, Encoding.UTF8.GetBytes(catalogJson));

        return Catalogue.ReadNode("node-1", record, TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void CatalogWithUses_SurfacesAllThreeLists()
    {
        var node = Read("""
            {
              "services": [ { "name": "orders.create" } ],
              "channels": [],
              "queues": [],
              "uses": {
                "services": ["payments.authorize"],
                "queues": ["notifications.email"],
                "channels": ["orders.placed"]
              }
            }
            """);

        node.Uses.Should().NotBeNull();
        node.Uses!.Services.Should().Equal("payments.authorize");
        node.Uses.Queues.Should().Equal("notifications.email");
        node.Uses.Channels.Should().Equal("orders.placed");

        // And the provides half is untouched by the addition.
        node.Services.Should().Equal("orders.create");
    }

    [Fact]
    public void Pre024Catalog_WithoutUses_ReadsAsEmpty_NotAsError()
    {
        var node = Read("""{ "services": [ { "name": "orders.create" } ], "channels": [] }""");

        node.Services.Should().Equal(new[] { "orders.create" }, "the old record must keep working untouched");
        node.Uses.Should().NotBeNull();
        node.Uses!.Services.Should().BeEmpty();
        node.Uses.Queues.Should().BeEmpty();
        node.Uses.Channels.Should().BeEmpty();
    }

    [Fact]
    public void MalformedUses_DegradesToEmpty_WithoutLosingTheNode()
    {
        // "uses" as a string instead of an object: tolerated, because a node with a broken
        // catalog fragment is a misconfiguration worth SEEING (022's error-handling rule).
        var node = Read("""{ "services": [], "channels": [], "uses": "not-an-object" }""");

        node.Name.Should().Be("node-1");
        node.Uses!.Services.Should().BeEmpty();
    }
}
