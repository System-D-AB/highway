using System.Text;
using FluentAssertions;
using Highway.Server.Internal;
using Xunit;

namespace Highway.Server.Tests;

/// <summary>
/// Task 3 — <see cref="HighwayKeys"/> string builders produce the documented key strings.
/// Tests are assertion-locked to the schema in design.md § "Key Schema".
/// </summary>
public class HighwayKeysTests
{
    // -------------------------------------------------------------------------
    // String builders
    // -------------------------------------------------------------------------

    [Fact]
    public void ServiceQueue_ProducesCorrectKey()
        => HighwayKeys.ServiceQueue("orders").Should().Be("hw:svc:orders:q");

    [Fact]
    public void ServiceProcessing_ProducesCorrectKey()
        => HighwayKeys.ServiceProcessing("orders", "node-1").Should().Be("hw:svc:orders:proc:node-1");

    [Fact]
    public void ServiceNodes_ProducesCorrectKey()
        => HighwayKeys.ServiceNodes("orders").Should().Be("hw:svc:orders:nodes");

    [Fact]
    public void ReplySlot_ProducesCorrectKey()
        => HighwayKeys.ReplySlot("req-42").Should().Be("hw:rep:req-42");

    [Fact]
    public void ChannelGroups_ProducesCorrectKey()
        => HighwayKeys.ChannelGroups("events").Should().Be("hw:ch:events:groups");

    [Fact]
    public void ChannelSeq_ProducesCorrectKey()
        => HighwayKeys.ChannelSeq("events").Should().Be("hw:ch:events:seq");

    [Fact]
    public void GroupQueue_ProducesCorrectKey()
        => HighwayKeys.GroupQueue("events", "grp-a").Should().Be("hw:ch:events:grp:grp-a:q");

    [Fact]
    public void GroupProcessing_ProducesCorrectKey()
        => HighwayKeys.GroupProcessing("events", "grp-a").Should().Be("hw:ch:events:grp:grp-a:proc");

    [Fact]
    public void ServiceDoorbell_ProducesCorrectKey()
        => HighwayKeys.ServiceDoorbell("orders").Should().Be("hw:door:svc:orders");

    [Fact]
    public void ReplyDoorbell_IsCorrect()
        => HighwayKeys.ReplyDoorbell.Should().Be("hw:door:rep");

    [Fact]
    public void GroupDoorbell_ProducesCorrectKey()
        => HighwayKeys.GroupDoorbell("events", "grp-a").Should().Be("hw:door:ch:events:grp:grp-a");

    // -------------------------------------------------------------------------
    // UTF-8 byte[] overloads match the string counterparts
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("orders")]
    [InlineData("my-service")]
    [InlineData("SVC_1")]
    public void ServiceQueueBytes_MatchesStringVersion(string svc)
        => Encoding.UTF8.GetString(HighwayKeys.ServiceQueueBytes(svc))
           .Should().Be(HighwayKeys.ServiceQueue(svc));

    [Fact]
    public void ReplyDoorbellBytes_MatchesConstant()
        => Encoding.UTF8.GetString(HighwayKeys.ReplyDoorbellBytes)
           .Should().Be(HighwayKeys.ReplyDoorbell);

    [Fact]
    public void GroupDoorbellBytes_MatchStringVersion()
        => Encoding.UTF8.GetString(HighwayKeys.GroupDoorbellBytes("ch", "grp"))
           .Should().Be(HighwayKeys.GroupDoorbell("ch", "grp"));

    // -------------------------------------------------------------------------
    // Registry keys (feature 006)
    // -------------------------------------------------------------------------

    [Fact]
    public void RegistrationNode_UsesRegistryNamespace()
        => HighwayKeys.RegistrationNode("node-1").Should().Be("hw:reg:node:node-1");

    [Fact]
    public void RegistrationNodeList_IsAFixedKey()
        => HighwayKeys.RegistrationNodeList.Should().Be("hw:reg:nodes");

    [Fact]
    public void RegistrationService_UsesRegistryNamespace()
        => HighwayKeys.RegistrationService("orders.create").Should().Be("hw:reg:svc:orders.create");

    [Fact]
    public void RegistryKeys_DoNotCollideWithQueueKeys()
    {
        // hw:reg:* must not overlap hw:svc:* / hw:ch:* from feature 004.
        HighwayKeys.RegistrationService("orders").Should().NotBe(HighwayKeys.ServiceQueue("orders"));
        HighwayKeys.RegistrationService("orders").Should().NotBe(HighwayKeys.ServiceNodes("orders"));
        HighwayKeys.RegistrationNode("n1").Should().StartWith("hw:reg:");
    }

    [Fact]
    public void RegistrationBytes_MatchStringVersions()
    {
        Encoding.UTF8.GetString(HighwayKeys.RegistrationNodeBytes("n1"))
            .Should().Be(HighwayKeys.RegistrationNode("n1"));
        Encoding.UTF8.GetString(HighwayKeys.RegistrationNodeListBytes)
            .Should().Be(HighwayKeys.RegistrationNodeList);
        Encoding.UTF8.GetString(HighwayKeys.RegistrationServiceBytes("svc"))
            .Should().Be(HighwayKeys.RegistrationService("svc"));
    }
}
