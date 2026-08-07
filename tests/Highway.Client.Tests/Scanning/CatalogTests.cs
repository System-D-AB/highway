using FluentAssertions;
using Highway.Abstractions;
using Highway.Client.Scanning;
using Highway.Client.Tests.TestFixtures;
using Xunit;

namespace Highway.Client.Tests.Scanning;

public class CatalogTests
{
    private readonly ImmutableCatalog _catalog;

    public CatalogTests()
    {
        var services = new List<ServiceDescriptor>
        {
            new()
            {
                Name = "test.hello",
                ImplementationType = typeof(TestService),
                RequestType = typeof(TestRequest),
                ResponseType = typeof(TestResponse),
                Lifetime = HighwayServiceLifetime.Scoped,
                IsInternal = false
            }
        };

        var channels = new List<ChannelDescriptor>
        {
            new()
            {
                Name = "test.events",
                MessageType = typeof(TestEvent),
                Subscribers = new List<SubscriberDescriptor>
                {
                    new() { ImplementationType = typeof(TestSubscriber), Lifetime = HighwayServiceLifetime.Scoped }
                },
                IsInternal = false
            }
        };

        _catalog = new ImmutableCatalog(services, channels);
    }

    [Fact]
    public void GetServiceDescriptor_ReturnsForKnownService()
    {
        var descriptor = _catalog.GetServiceDescriptor("test.hello");

        descriptor.Should().NotBeNull();
        descriptor!.ImplementationType.Should().Be(typeof(TestService));
        descriptor.RequestType.Should().Be(typeof(TestRequest));
        descriptor.ResponseType.Should().Be(typeof(TestResponse));
    }

    [Fact]
    public void GetServiceDescriptor_ReturnsNullForUnknown()
    {
        _catalog.GetServiceDescriptor("nonexistent").Should().BeNull();
    }

    [Fact]
    public void GetServiceDescriptor_CaseInsensitiveLookup()
    {
        _catalog.GetServiceDescriptor("TEST.HELLO").Should().NotBeNull();
        _catalog.GetServiceDescriptor("Test.Hello").Should().NotBeNull();
    }

    [Fact]
    public void GetChannelDescriptor_ReturnsForKnownChannel()
    {
        var descriptor = _catalog.GetChannelDescriptor("test.events");

        descriptor.Should().NotBeNull();
        descriptor!.MessageType.Should().Be(typeof(TestEvent));
        descriptor.Subscribers.Should().HaveCount(1);
    }

    [Fact]
    public void GetChannelDescriptor_ReturnsNullForUnknown()
    {
        _catalog.GetChannelDescriptor("nonexistent").Should().BeNull();
    }

    [Fact]
    public void GetServiceNameForRequestType_Works()
    {
        _catalog.GetServiceNameForRequestType(typeof(TestRequest)).Should().Be("test.hello");
    }

    [Fact]
    public void GetServiceNameForRequestType_ReturnsNullForUnknown()
    {
        _catalog.GetServiceNameForRequestType(typeof(object)).Should().BeNull();
    }

    [Fact]
    public void GetChannelNameForMessageType_Works()
    {
        _catalog.GetChannelNameForMessageType(typeof(TestEvent)).Should().Be("test.events");
    }

    [Fact]
    public void GetChannelNameForMessageType_ReturnsNullForUnknown()
    {
        _catalog.GetChannelNameForMessageType(typeof(object)).Should().BeNull();
    }

    [Fact]
    public void IsServiceRegistered_ReturnsTrueForKnown()
    {
        _catalog.IsServiceRegistered("test.hello").Should().BeTrue();
    }

    [Fact]
    public void IsServiceRegistered_ReturnsFalseForUnknown()
    {
        _catalog.IsServiceRegistered("unknown").Should().BeFalse();
    }

    [Fact]
    public void IsChannelRegistered_ReturnsTrueForKnown()
    {
        _catalog.IsChannelRegistered("test.events").Should().BeTrue();
    }

    [Fact]
    public void IsChannelRegistered_ReturnsFalseForUnknown()
    {
        _catalog.IsChannelRegistered("unknown").Should().BeFalse();
    }

    [Fact]
    public void AllServices_ReturnsAllRegistered()
    {
        _catalog.AllServices.Should().HaveCount(1);
        _catalog.AllServices[0].Name.Should().Be("test.hello");
    }

    [Fact]
    public void AllChannels_ReturnsAllRegistered()
    {
        _catalog.AllChannels.Should().HaveCount(1);
        _catalog.AllChannels[0].Name.Should().Be("test.events");
    }

    [Fact]
    public void ToCatalogInfo_IncludesPublicServices()
    {
        var info = _catalog.ToCatalogInfo();

        info.Services.Should().ContainSingle(s => s.Name == "test.hello");
        info.Channels.Should().ContainSingle(c => c.Name == "test.events");
    }

    [Fact]
    public void ToCatalogInfo_ExcludesInternalServices()
    {
        var services = new List<ServiceDescriptor>
        {
            new()
            {
                Name = "internal.service",
                ImplementationType = typeof(TestService),
                RequestType = typeof(TestRequest),
                ResponseType = typeof(TestResponse),
                Lifetime = HighwayServiceLifetime.Scoped,
                IsInternal = true
            }
        };

        var catalog = new ImmutableCatalog(services, []);
        var info = catalog.ToCatalogInfo();

        info.Services.Should().BeEmpty();
    }
}
