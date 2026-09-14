using FluentAssertions;
using Xunit;

namespace Highway.Client.Hosting.Tests;

public class ServiceIdentityTests
{
    // --- Convention defaults (no overrides) ---

    [Fact]
    public void Resolve_NoOverrides_UsesConventionName()
    {
        var result = ServiceIdentity.Resolve(new VerbOptions(), null);
        result.IsOk.Should().BeTrue();
        // Convention name comes from entry assembly — in test it's the test runner,
        // so just verify it's non-empty.
        result.Identity!.Name.Should().NotBeNullOrWhiteSpace();
    }

    // --- Verb option wins over code option ---

    [Fact]
    public void Resolve_VerbOptionOverridesCodeOption()
    {
        var verbOptions = new VerbOptions { Name = "verb-name" };
        var codeOptions = new HostingOptions { ServiceName = "code-name" };

        var result = ServiceIdentity.Resolve(verbOptions, codeOptions);
        result.IsOk.Should().BeTrue();
        result.Identity!.Name.Should().Be("verb-name");
    }

    // --- Code option used when verb option absent ---

    [Fact]
    public void Resolve_CodeOptionUsedWhenVerbAbsent()
    {
        var verbOptions = new VerbOptions();
        var codeOptions = new HostingOptions { ServiceName = "code-name" };

        var result = ServiceIdentity.Resolve(verbOptions, codeOptions);
        result.IsOk.Should().BeTrue();
        result.Identity!.Name.Should().Be("code-name");
    }

    // --- Display name defaults to name ---

    [Fact]
    public void Resolve_DisplayNameDefaultsToName()
    {
        var verbOptions = new VerbOptions { Name = "mysvc" };

        var result = ServiceIdentity.Resolve(verbOptions, null);
        result.IsOk.Should().BeTrue();
        result.Identity!.DisplayName.Should().Be("mysvc");
    }

    // --- Display name override ---

    [Fact]
    public void Resolve_DisplayNameOverride()
    {
        var verbOptions = new VerbOptions { Name = "mysvc", DisplayName = "My Service" };

        var result = ServiceIdentity.Resolve(verbOptions, null);
        result.IsOk.Should().BeTrue();
        result.Identity!.DisplayName.Should().Be("My Service");
    }

    // --- Description defaults to display name ---

    [Fact]
    public void Resolve_DescriptionDefaultsToDisplayName()
    {
        var verbOptions = new VerbOptions { Name = "mysvc", DisplayName = "My Service" };

        var result = ServiceIdentity.Resolve(verbOptions, null);
        result.IsOk.Should().BeTrue();
        result.Identity!.Description.Should().Be("My Service");
    }

    // --- Description override ---

    [Fact]
    public void Resolve_DescriptionOverride()
    {
        var verbOptions = new VerbOptions
        {
            Name = "mysvc",
            Description = "A custom description"
        };

        var result = ServiceIdentity.Resolve(verbOptions, null);
        result.Identity!.Description.Should().Be("A custom description");
    }

    // --- User passthrough ---

    [Fact]
    public void Resolve_UserFromVerbOption()
    {
        var verbOptions = new VerbOptions { Name = "mysvc", User = "www-data" };

        var result = ServiceIdentity.Resolve(verbOptions, null);
        result.Identity!.User.Should().Be("www-data");
    }

    [Fact]
    public void Resolve_UserFromCodeOption()
    {
        var verbOptions = new VerbOptions { Name = "mysvc" };
        var codeOptions = new HostingOptions { User = "svcuser" };

        var result = ServiceIdentity.Resolve(verbOptions, codeOptions);
        result.Identity!.User.Should().Be("svcuser");
    }

    // --- Validation: empty name ---

    [Fact]
    public void Resolve_EmptyName_Fails()
    {
        var verbOptions = new VerbOptions { Name = "" };

        var result = ServiceIdentity.Resolve(verbOptions, null);
        result.IsOk.Should().BeFalse();
        result.Error.Should().Contain("empty");
    }

    [Fact]
    public void Resolve_WhitespaceName_Fails()
    {
        var verbOptions = new VerbOptions { Name = "   " };

        var result = ServiceIdentity.Resolve(verbOptions, null);
        result.IsOk.Should().BeFalse();
        result.Error.Should().Contain("empty");
    }

    // --- Validation: name too long ---

    [Fact]
    public void Resolve_NameTooLong_Fails()
    {
        var verbOptions = new VerbOptions { Name = new string('a', 257) };

        var result = ServiceIdentity.Resolve(verbOptions, null);
        result.IsOk.Should().BeFalse();
        result.Error.Should().Contain("256");
    }

    [Fact]
    public void Resolve_NameExactly256_Succeeds()
    {
        var verbOptions = new VerbOptions { Name = new string('a', 256) };

        var result = ServiceIdentity.Resolve(verbOptions, null);
        result.IsOk.Should().BeTrue();
    }

    // --- Validation: invalid characters ---

    [Theory]
    [InlineData("my service")]   // space
    [InlineData("my/service")]   // slash
    [InlineData("my\\service")]  // backslash
    [InlineData("my:service")]   // colon
    [InlineData("my@service")]   // at
    public void Resolve_InvalidCharactersInName_Fails(string name)
    {
        var verbOptions = new VerbOptions { Name = name };

        var result = ServiceIdentity.Resolve(verbOptions, null);
        result.IsOk.Should().BeFalse();
        result.Error.Should().Contain("invalid characters");
    }

    // --- Validation: valid names ---

    [Theory]
    [InlineData("mysvc")]
    [InlineData("my-svc")]
    [InlineData("my_svc")]
    [InlineData("my.svc")]
    [InlineData("MySvc123")]
    [InlineData("highway-broker")]
    public void Resolve_ValidNames_Succeed(string name)
    {
        var verbOptions = new VerbOptions { Name = name };

        var result = ServiceIdentity.Resolve(verbOptions, null);
        result.IsOk.Should().BeTrue();
        result.Identity!.Name.Should().Be(name);
    }

    // --- Validation: empty user ---

    [Fact]
    public void Resolve_EmptyUser_Fails()
    {
        var verbOptions = new VerbOptions { Name = "mysvc", User = "  " };

        var result = ServiceIdentity.Resolve(verbOptions, null);
        result.IsOk.Should().BeFalse();
        result.Error.Should().Contain("User");
    }

    // --- Full precedence chain ---

    [Fact]
    public void Resolve_FullPrecedenceChain()
    {
        // Verb overrides code, code overrides convention
        var verbOptions = new VerbOptions { Name = "verb-name", DisplayName = "Verb Display" };
        var codeOptions = new HostingOptions
        {
            ServiceName = "code-name",
            DisplayName = "Code Display",
            Description = "Code Description"
        };

        var result = ServiceIdentity.Resolve(verbOptions, codeOptions);
        result.IsOk.Should().BeTrue();
        result.Identity!.Name.Should().Be("verb-name");
        result.Identity.DisplayName.Should().Be("Verb Display");
        // Description falls through: verb=null → code="Code Description"
        result.Identity.Description.Should().Be("Code Description");
    }
}
