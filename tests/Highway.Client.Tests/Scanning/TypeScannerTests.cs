using FluentAssertions;
using Highway.Abstractions;
using Highway.Abstractions.Exceptions;
using Highway.Client.Scanning;
using Highway.Client.Tests.TestFixtures;
using Highway.Client.Tests.TestFixtures.Invalid;
using Xunit;

namespace Highway.Client.Tests.Scanning;

public class TypeScannerTests
{
    private readonly DefaultTypeScanner _scanner = new();

    [Fact]
    public void Scan_DiscoversValidService()
    {
        var types = new List<Type> { typeof(TestService) };
        var result = _scanner.ScanTypes(types);

        result.Services.Should().ContainSingle(s => s.Name == "test.hello");
        var service = result.Services[0];
        service.ImplementationType.Should().Be(typeof(TestService));
        service.RequestType.Should().Be(typeof(TestRequest));
        service.ResponseType.Should().Be(typeof(TestResponse));
    }

    [Fact]
    public void Scan_DiscoversChannelWithMultipleSubscribers()
    {
        var types = new List<Type> { typeof(TestSubscriber), typeof(TestSubscriber2) };
        var result = _scanner.ScanTypes(types);

        result.Channels.Should().ContainSingle(c => c.Name == "test.events");
        var channel = result.Channels[0];
        channel.MessageType.Should().Be(typeof(TestEvent));
        channel.Subscribers.Should().HaveCount(2);
    }

    [Fact]
    public void Scan_MultipleSubscribers_AllRegistered()
    {
        var types = new List<Type> { typeof(TestSubscriber), typeof(TestSubscriber2) };
        var result = _scanner.ScanTypes(types);
        var channel = result.Channels[0];

        channel.Subscribers.Should().Contain(s => s.ImplementationType == typeof(TestSubscriber));
        channel.Subscribers.Should().Contain(s => s.ImplementationType == typeof(TestSubscriber2));
    }

    [Fact]
    public void Scan_MissingServiceAttribute_Throws()
    {
        var types = new List<Type> { typeof(NoAttributeService) };
        var act = () => _scanner.ScanTypes(types);

        act.Should().Throw<ServiceAttributeNotFoundException>();
    }

    [Fact]
    public void Scan_MissingChannelAttribute_Throws()
    {
        var types = new List<Type> { typeof(NoChannelSubscriber) };
        var act = () => _scanner.ScanTypes(types);

        act.Should().Throw<ChannelAttributeMissingException>();
    }

    [Fact]
    public void Scan_DuplicateServiceName_Throws()
    {
        var types = new List<Type> { typeof(TestService), typeof(DuplicateService) };
        var act = () => _scanner.ScanTypes(types);

        act.Should().Throw<ServiceWithSameNameAlreadyExistsException>();
    }

    [Fact]
    public void Scan_DefaultLifetime_IsScoped()
    {
        var types = new List<Type> { typeof(TestService) };
        var result = _scanner.ScanTypes(types);

        result.Services[0].Lifetime.Should().Be(HighwayServiceLifetime.Scoped);
    }

    [Fact]
    public void Scan_EmptyTypeList_ReturnsEmptyResult()
    {
        var types = new List<Type>();
        var result = _scanner.ScanTypes(types);

        result.Services.Should().BeEmpty();
        result.Channels.Should().BeEmpty();
    }

    [Fact]
    public void Scan_AbstractTypes_AreIgnored()
    {
        // Abstract types should be filtered out
        var types = new List<Type> { typeof(AsyncService<TestRequest, TestResponse>) };
        var result = _scanner.ScanTypes(types);

        result.Services.Should().BeEmpty();
    }

    [Fact]
    public void Scan_MixedServicesAndChannels_DiscoversBoth()
    {
        var types = new List<Type> { typeof(TestService), typeof(TestSubscriber) };
        var result = _scanner.ScanTypes(types);

        result.Services.Should().HaveCount(1);
        result.Channels.Should().HaveCount(1);
    }

    // -------------------------------------------------------------------------
    // Feature 005 Task 3 — response-type parameterless-constructor validation
    // -------------------------------------------------------------------------

    [Fact]
    public void Scan_ResponseWithoutParameterlessCtor_Throws()
    {
        var types = new List<Type> { typeof(NoParameterlessCtorService) };
        var act = () => _scanner.ScanTypes(types);

        act.Should().Throw<ResponseTypeRequiresParameterlessConstructorException>();
    }

    [Fact]
    public void Scan_ResponseWithParameterlessCtor_Passes()
    {
        var types = new List<Type> { typeof(TestService) };
        var act = () => _scanner.ScanTypes(types);

        act.Should().NotThrow();
    }
}
