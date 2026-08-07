using FluentAssertions;
using Highway.Client.Engine;
using Xunit;

namespace Highway.Client.Tests.Engine;

/// <summary>
/// Feature 012 T7 — credentials must not appear in a log entry or an exception message.
///
/// <para>Three sites leaked before this existed. None were dangerous while Highway had no
/// authentication; all three became credential leaks the moment it did.</para>
/// </summary>
public class CredentialRedactionTests
{
    [Theory]
    [InlineData("localhost:6500,password=s3cret", "localhost:6500,password=***")]
    [InlineData("localhost:6500,user=admin,password=s3cret", "localhost:6500,user=***,password=***")]
    [InlineData("localhost:6500,PASSWORD=s3cret", "localhost:6500,PASSWORD=***")]
    [InlineData("localhost:6500, password = s3cret ", "localhost:6500, password =***")]
    [InlineData("localhost:6500,pwd=s3cret", "localhost:6500,pwd=***")]
    public void Redact_RemovesCredentials(string input, string expected)
        => ConnectionStringRedactor.Redact(input).Should().Be(expected);

    [Theory]
    [InlineData("localhost:6500")]
    [InlineData("localhost:6500,abortConnect=false")]
    [InlineData("localhost:6500,ssl=true,sslHost=highway.internal")]
    public void Redact_LeavesNonSecretsIntact(string input)
        => ConnectionStringRedactor.Redact(input).Should().Be(input);

    /// <summary>
    /// A redactor that throws on malformed input leaks on malformed input — which is
    /// exactly when it matters most, because the parse-failure path is the one carrying a
    /// value nobody could read.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData(",,,")]
    [InlineData("=")]
    [InlineData("not a connection string at all")]
    public void Redact_NeverThrows(string input)
        => FluentActions.Invoking(() => ConnectionStringRedactor.Redact(input)).Should().NotThrow();

    [Fact]
    public void Redact_HandlesNull()
        => ConnectionStringRedactor.Redact(null).Should().BeEmpty();

    /// <summary>The property that matters, stated directly: the secret is gone.</summary>
    [Fact]
    public void Redact_TheSecretIsAbsent()
        => ConnectionStringRedactor
            .Redact("localhost:6500,user=admin,password=hunter2,abortConnect=true")
            .Should().NotContain("hunter2").And.NotContain("admin");
}
