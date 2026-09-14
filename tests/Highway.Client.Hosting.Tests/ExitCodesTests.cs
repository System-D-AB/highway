using FluentAssertions;
using Xunit;

namespace Highway.Client.Hosting.Tests;

/// <summary>
/// Guard: exit code values must not change — scripts and service managers
/// branch on the numbers from 031. These tests break if a value moves.
/// </summary>
public class ExitCodesTests
{
    [Fact]
    public void Success_Is_0() => ExitCodes.Success.Should().Be(0);

    [Fact]
    public void Unexpected_Is_1() => ExitCodes.Unexpected.Should().Be(1);

    [Fact]
    public void ConfigurationInvalid_Is_2() => ExitCodes.ConfigurationInvalid.Should().Be(2);

    [Fact]
    public void DataDirectoryUnusable_Is_3() => ExitCodes.DataDirectoryUnusable.Should().Be(3);

    [Fact]
    public void PrivilegeInsufficient_Is_4() => ExitCodes.PrivilegeInsufficient.Should().Be(4);

    [Fact]
    public void ServiceStateConflict_Is_5() => ExitCodes.ServiceStateConflict.Should().Be(5);

    [Fact]
    public void PlatformUnsupported_Is_6() => ExitCodes.PlatformUnsupported.Should().Be(6);

    [Fact]
    public void InvalidArguments_Is_7() => ExitCodes.InvalidArguments.Should().Be(7);

    [Fact]
    public void AllCodesAreDistinct()
    {
        var codes = new[]
        {
            ExitCodes.Success,
            ExitCodes.Unexpected,
            ExitCodes.ConfigurationInvalid,
            ExitCodes.DataDirectoryUnusable,
            ExitCodes.PrivilegeInsufficient,
            ExitCodes.ServiceStateConflict,
            ExitCodes.PlatformUnsupported,
            ExitCodes.InvalidArguments,
        };

        codes.Should().OnlyHaveUniqueItems();
    }
}
