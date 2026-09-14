using FluentAssertions;
using Xunit;

namespace Highway.Client.Hosting.Tests;

public class VerbParserTests
{
    // --- No verb: returns null ---

    [Fact]
    public void Parse_EmptyArgs_ReturnsNull()
    {
        VerbParser.Parse([]).Should().BeNull();
    }

    [Fact]
    public void Parse_NonVerbFirstArg_ReturnsNull()
    {
        VerbParser.Parse(["--config", "foo.json"]).Should().BeNull();
    }

    [Fact]
    public void Parse_UnknownFirstArg_ReturnsNull()
    {
        VerbParser.Parse(["run"]).Should().BeNull();
    }

    // --- Five verbs recognized ---

    [Theory]
    [InlineData("install")]
    [InlineData("uninstall")]
    [InlineData("start")]
    [InlineData("stop")]
    [InlineData("status")]
    public void Parse_RecognizedVerb_BareWord(string verb)
    {
        var result = VerbParser.Parse([verb]);
        result.Should().NotBeNull();
        result!.IsError.Should().BeFalse();
        result.Verb.Should().Be(verb);
    }

    [Theory]
    [InlineData("Install")]
    [InlineData("INSTALL")]
    [InlineData("Uninstall")]
    [InlineData("START")]
    [InlineData("Stop")]
    [InlineData("STATUS")]
    public void Parse_VerbIsCaseInsensitive(string verb)
    {
        var result = VerbParser.Parse([verb]);
        result.Should().NotBeNull();
        result!.IsError.Should().BeFalse();
        result.Verb.Should().Be(verb.ToLowerInvariant());
    }

    // --- --name option ---

    [Theory]
    [InlineData("install")]
    [InlineData("uninstall")]
    [InlineData("start")]
    [InlineData("stop")]
    [InlineData("status")]
    public void Parse_NameOption_AllVerbs(string verb)
    {
        var result = VerbParser.Parse([verb, "--name", "MyService"]);
        result.Should().NotBeNull();
        result!.IsError.Should().BeFalse();
        result.Options.Name.Should().Be("MyService");
    }

    [Fact]
    public void Parse_NameOption_MissingValue_Error()
    {
        var result = VerbParser.Parse(["install", "--name"]);
        result.Should().NotBeNull();
        result!.IsError.Should().BeTrue();
        result.Error.Should().Contain("--name");
        result.ExitCode.Should().Be(ExitCodes.InvalidArguments);
    }

    // --- --display-name option ---

    [Fact]
    public void Parse_DisplayNameOption()
    {
        var result = VerbParser.Parse(["install", "--display-name", "My Display Name"]);
        result.Should().NotBeNull();
        result!.IsError.Should().BeFalse();
        result.Options.DisplayName.Should().Be("My Display Name");
    }

    [Fact]
    public void Parse_DisplayNameOption_MissingValue_Error()
    {
        var result = VerbParser.Parse(["install", "--display-name"]);
        result.Should().NotBeNull();
        result!.IsError.Should().BeTrue();
        result.Error.Should().Contain("--display-name");
    }

    // --- --description option ---

    [Fact]
    public void Parse_DescriptionOption()
    {
        var result = VerbParser.Parse(["install", "--description", "A test service"]);
        result!.Options.Description.Should().Be("A test service");
    }

    [Fact]
    public void Parse_DescriptionOption_MissingValue_Error()
    {
        var result = VerbParser.Parse(["install", "--description"]);
        result!.IsError.Should().BeTrue();
        result.Error.Should().Contain("--description");
    }

    // --- --user option (install only) ---

    [Fact]
    public void Parse_UserOption_Install()
    {
        var result = VerbParser.Parse(["install", "--user", "www-data"]);
        result!.IsError.Should().BeFalse();
        result.Options.User.Should().Be("www-data");
    }

    [Theory]
    [InlineData("uninstall")]
    [InlineData("start")]
    [InlineData("stop")]
    [InlineData("status")]
    public void Parse_UserOption_NonInstallVerb_Error(string verb)
    {
        var result = VerbParser.Parse([verb, "--user", "www-data"]);
        result!.IsError.Should().BeTrue();
        result.Error.Should().Contain("--user");
        result.Error.Should().Contain("install");
    }

    // --- --start option (install only) ---

    [Fact]
    public void Parse_StartOption_Install()
    {
        var result = VerbParser.Parse(["install", "--start"]);
        result!.IsError.Should().BeFalse();
        result.Options.StartAfterInstall.Should().BeTrue();
    }

    [Theory]
    [InlineData("uninstall")]
    [InlineData("start")]
    [InlineData("stop")]
    [InlineData("status")]
    public void Parse_StartOption_NonInstallVerb_Error(string verb)
    {
        var result = VerbParser.Parse([verb, "--start"]);
        result!.IsError.Should().BeTrue();
        result.Error.Should().Contain("--start");
    }

    // --- Passthrough arguments (-- separator) ---

    [Fact]
    public void Parse_Passthrough_Install()
    {
        var result = VerbParser.Parse(["install", "--name", "svc", "--", "--config", "/etc/app.json", "--port", "9999"]);
        result!.IsError.Should().BeFalse();
        result.Options.Name.Should().Be("svc");
        result.PassthroughArgs.Should().Equal("--config", "/etc/app.json", "--port", "9999");
    }

    [Fact]
    public void Parse_Passthrough_Empty()
    {
        var result = VerbParser.Parse(["install", "--"]);
        result!.IsError.Should().BeFalse();
        result.PassthroughArgs.Should().BeEmpty();
    }

    [Theory]
    [InlineData("uninstall")]
    [InlineData("start")]
    [InlineData("stop")]
    [InlineData("status")]
    public void Parse_Passthrough_NonInstallVerb_Error(string verb)
    {
        var result = VerbParser.Parse([verb, "--", "foo"]);
        result!.IsError.Should().BeTrue();
        result.Error.Should().Contain("--");
        result.Error.Should().Contain("install");
    }

    // --- Unknown option ---

    [Fact]
    public void Parse_UnknownOption_Error()
    {
        var result = VerbParser.Parse(["install", "--bogus"]);
        result!.IsError.Should().BeTrue();
        result.Error.Should().Contain("--bogus");
        result.ExitCode.Should().Be(ExitCodes.InvalidArguments);
    }

    // --- Combined options ---

    [Fact]
    public void Parse_AllInstallOptions()
    {
        var result = VerbParser.Parse([
            "install",
            "--name", "mysvc",
            "--display-name", "My Service",
            "--description", "A service that does things",
            "--user", "svcuser",
            "--start",
            "--", "--config", "/etc/app.json"
        ]);

        result!.IsError.Should().BeFalse();
        result.Verb.Should().Be("install");
        result.Options.Name.Should().Be("mysvc");
        result.Options.DisplayName.Should().Be("My Service");
        result.Options.Description.Should().Be("A service that does things");
        result.Options.User.Should().Be("svcuser");
        result.Options.StartAfterInstall.Should().BeTrue();
        result.PassthroughArgs.Should().Equal("--config", "/etc/app.json");
    }

    // --- Default options when not specified ---

    [Fact]
    public void Parse_NoOptions_AllNull()
    {
        var result = VerbParser.Parse(["install"]);
        result!.Options.Name.Should().BeNull();
        result.Options.DisplayName.Should().BeNull();
        result.Options.Description.Should().BeNull();
        result.Options.User.Should().BeNull();
        result.Options.StartAfterInstall.Should().BeFalse();
        result.PassthroughArgs.Should().BeEmpty();
    }

    // --- Value that looks like a flag ---

    [Fact]
    public void Parse_NameValueStartingWithDash_Error()
    {
        // "--name --foo" → --foo looks like a flag, not a value
        var result = VerbParser.Parse(["install", "--name", "--foo"]);
        result!.IsError.Should().BeTrue();
        result.Error.Should().Contain("--name");
    }
}
