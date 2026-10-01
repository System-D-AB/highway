using FluentAssertions;
using Highway.Server.Internal;
using Xunit;

namespace Highway.Server.Tests;

/// <summary>
/// The doorbell channel names are wire contract (clients subscribe to them); the protocol file's doorbell
/// section defines them. Storage key names are private to the broker and not tested here (070).
/// </summary>
public class HighwayKeysTests
{
    [Fact]
    public void QueueDoorbell_IsTheDocumentedChannel()
        => HighwayKeys.QueueDoorbell("news@billing").Should().Be("hw:door:q:news@billing");

    [Fact]
    public void ServiceDoorbell_IsTheDocumentedChannel()
        => HighwayKeys.ServiceDoorbell("orders").Should().Be("hw:door:svc:orders");

    [Fact]
    public void ReplyDoorbell_IsTheDocumentedChannel()
        => HighwayKeys.ReplyDoorbell.Should().Be("hw:door:rep");
}
