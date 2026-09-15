using FluentAssertions;
using Highway.Server.Security;
using Xunit;

namespace Highway.Server.Tests;

/// <summary>040 T6 — the PBKDF2 password-hash recipe (037 R11): mint, verify, reject.</summary>
public class PasswordHashTests
{
    [Fact]
    public void Hash_ThenVerify_Succeeds()
    {
        var hash = PasswordHash.Hash("s3cr3t");
        PasswordHash.Verify("s3cr3t", hash).Should().BeTrue();
    }

    [Fact]
    public void Verify_WrongPassword_Fails()
    {
        var hash = PasswordHash.Hash("s3cr3t");
        PasswordHash.Verify("nope", hash).Should().BeFalse();
    }

    [Fact]
    public void Hash_Format_IsSelfDescribing()
    {
        var hash = PasswordHash.Hash("pw", iterations: 150_000);
        var parts = hash.Split('$');
        parts.Should().HaveCount(4);
        parts[0].Should().Be("PBKDF2");
        parts[1].Should().Be("150000", "the iteration count travels in the hash");
        PasswordHash.IsHash(hash).Should().BeTrue();
    }

    [Fact]
    public void Verify_HonoursTheEmbeddedIterationCount()
    {
        // A hash minted at a different work factor still verifies — the count is read from the hash,
        // not assumed, so raising DefaultIterations later does not strand existing users.
        var hash = PasswordHash.Hash("pw", iterations: 100_000);
        PasswordHash.Verify("pw", hash).Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("plaintext")]
    [InlineData("PBKDF2$notanumber$salt$key")]
    [InlineData("PBKDF2$1000$not-base64!$key")]
    [InlineData("SCRYPT$1000$c2FsdA==$a2V5")]
    public void Verify_MalformedOrUnknownScheme_FailsClosed_NeverThrows(string hash)
    {
        var act = () => PasswordHash.Verify("pw", hash);
        act.Should().NotThrow();
        PasswordHash.Verify("pw", hash).Should().BeFalse();
    }

    [Fact]
    public void IsHash_DistinguishesHashFromPlaintext()
    {
        PasswordHash.IsHash(PasswordHash.Hash("pw")).Should().BeTrue();
        PasswordHash.IsHash("just-a-password").Should().BeFalse();
        PasswordHash.IsHash(null).Should().BeFalse();
    }
}
