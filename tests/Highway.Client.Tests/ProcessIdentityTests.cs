using System.Text;
using System.Text.Json;
using FluentAssertions;
using Highway.Client.Engine;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Highway.Client.Tests;

/// <summary>
/// Feature 072: a default node name that is unique per hosted process, a default subscription group that
/// keeps its pre-072 value, and an instance id on every connection so the broker can count processes.
/// </summary>
public class ProcessIdentityTests
{
    private static HighwayOptions Options(string? hosting)
    {
        var o = new HighwayOptions();
        o.HostingDetector = () => hosting;
        return o;
    }

    // ---- R1: the default node name ------------------------------------------------------------

    [Fact]
    public void AHostedProcess_GetsItsHostingNameInTheDefault()
    {
        var o = Options("shop2");

        o.NodeName.Should().Be(HighwayOptions.BaseName() + "-shop2");
        o.NodeNameIsDefault.Should().BeTrue();
    }

    [Fact]
    public void AnUnhostedProcess_KeepsThePre072Default()
    {
        var o = Options(null);

        o.NodeName.Should().Be(HighwayOptions.BaseName());
        o.NodeName.Should().Be($"{System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name ?? "highway-node"}-{Environment.MachineName}");
    }

    [Fact]
    public void AnExplicitNodeName_IsNeverAltered_AndDetectionNeverRuns()
    {
        var detected = false;
        var o = new HighwayOptions();
        o.HostingDetector = () => { detected = true; return "shop2"; };

        o.NodeName = "orders-1";

        o.NodeName.Should().Be("orders-1");
        o.NodeNameIsDefault.Should().BeFalse();
        detected.Should().BeFalse("an application that names itself pays nothing for detection");
    }

    [Fact]
    public void AHostingNameTheServerWouldReject_IsMadeValid()
    {
        var o = Options("svc@a\u0001b\u007f");

        o.NodeName.Should().EndWith("-svc_a_b_");
        var validate = () => HighwayOptionsValidator.ValidateNodeName(o.NodeName);
        validate.Should().NotThrow();
    }

    [Fact]
    public void ALongComposedName_IsCutToTheIdentifierCap_OnACharacterBoundary()
    {
        var composed = ProcessHosting.Compose("app", new string('é', 300));   // 2 bytes each

        Encoding.UTF8.GetByteCount(composed).Should().BeLessThanOrEqualTo(HighwayOptionsValidator.MaxNodeNameBytes);
        composed.Should().StartWith("app-é").And.NotContain("�");
        var validate = () => HighwayOptionsValidator.ValidateNodeName(composed);
        validate.Should().NotThrow();
    }

    [Fact]
    public void Detection_OnATestProcess_NeverThrows()
    {
        // Not a service; APP_POOL_ID is normally unset. Whatever the machine, detection answers.
        var detect = () => ProcessHosting.DetectHostingName();
        detect.Should().NotThrow();
        if (Environment.GetEnvironmentVariable("APP_POOL_ID") is null)
            detect().Should().BeNull("a test runner is neither a Windows service nor an IIS worker");
    }

    // ---- R2: the default subscription group ---------------------------------------------------

    [Fact]
    public void BothDefaulted_TheGroupIsThePre072DefaultName()
    {
        // Copies of one application on one machine must keep sharing one group (and so keep splitting its
        // messages) even though each now has its own node name. {channel}@{group} is byte-identical.
        var shop1 = Options("shop1");
        var shop2 = Options("shop2");

        shop1.NodeName.Should().NotBe(shop2.NodeName);
        shop1.EffectiveSubscriptionGroup.Should().Be(HighwayOptions.BaseName())
            .And.Be(shop2.EffectiveSubscriptionGroup);
    }

    [Fact]
    public void AnExplicitNodeName_IsTheGroup_AsBefore()
    {
        var o = Options("shop2");
        o.NodeName = "orders-1";

        o.EffectiveSubscriptionGroup.Should().Be("orders-1");
    }

    [Fact]
    public void AnExplicitGroup_IsNeverAltered()
    {
        var o = Options("shop2");
        o.SubscriptionGroup = "billing";

        o.EffectiveSubscriptionGroup.Should().Be("billing");
    }

    // ---- R3: the instance id ------------------------------------------------------------------

    [Fact]
    public void TheInstanceId_Is12LowercaseHex_AndDiffersPerEngine()
    {
        var a = new HighwayOptions().InstanceId;
        var b = new HighwayOptions().InstanceId;

        a.Should().MatchRegex("^[0-9a-f]{12}$");
        a.Should().NotBe(b);
    }

    [Fact]
    public void EveryConnection_IsNamedNodeAtInstance()
    {
        var o = new HighwayOptions { NodeName = "web shop", Server = "127.0.0.1:6500" };

        var config = HighwayConnectionConfiguration.Build(o.Server!, o);

        config.ClientName.Should().Be($"web_shop@{o.InstanceId}",
            "whitespace is sanitised as before, and the suffix lets the broker count processes");
    }

    [Fact]
    public async Task TheSession_DescribesTheProcess()
    {
        var connection = Substitute.For<IHighwayConnection>();
        byte[]? sent = null;
        connection.EstablishSessionAsync("node-1", Arg.Do<byte[]>(b => sent = b), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        var o = Options("shop2");
        var session = new SessionKeeper(connection, "node-1", catalog: null, [], [], NullLogger.Instance,
            SessionKeeper.Instance.Of(o));

        await session.EstablishAsync(CancellationToken.None);

        using var doc = JsonDocument.Parse(sent!);
        var instance = doc.RootElement.GetProperty("instance");
        instance.GetProperty("id").GetString().Should().Be(o.InstanceId);
        instance.GetProperty("host").GetString().Should().Be(Environment.MachineName);
        instance.GetProperty("pid").GetInt32().Should().Be(Environment.ProcessId);
        instance.GetProperty("hosting").GetString().Should().Be("shop2");
        instance.GetProperty("client").GetString().Should().NotContain("+", "the commit hash is not a version");
        DateTimeOffset.Parse(instance.GetProperty("started").GetString()!).Should().BeBefore(DateTimeOffset.UtcNow.AddSeconds(1));
    }

    [Fact]
    public async Task TheSession_DeclaresDeadLetters()
    {
        var connection = Substitute.For<IHighwayConnection>();
        byte[]? sent = null;
        connection.EstablishSessionAsync("node-1", Arg.Do<byte[]>(b => sent = b), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var deadLetters = new List<SessionKeeper.DeadLetterDeclaration>
        {
            new("Q", "orders"),
            new("SVC", "payment"),
            new("Q", "events@billing")
        };

        var session = new SessionKeeper(
            connection, "node-1", catalog: null, [], [], NullLogger.Instance,
            deadLetters: deadLetters);

        await session.EstablishAsync(CancellationToken.None);

        using var doc = JsonDocument.Parse(sent!);
        var array = doc.RootElement.GetProperty("deadLetters");
        array.GetArrayLength().Should().Be(3);
        array[0].GetProperty("kind").GetString().Should().Be("Q");
        array[0].GetProperty("name").GetString().Should().Be("orders");
        array[1].GetProperty("kind").GetString().Should().Be("SVC");
        array[1].GetProperty("name").GetString().Should().Be("payment");
        array[2].GetProperty("kind").GetString().Should().Be("Q");
        array[2].GetProperty("name").GetString().Should().Be("events@billing");
    }
}
