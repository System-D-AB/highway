using FluentAssertions;
using Highway.Server.Internal;
using Xunit;

namespace Highway.Server.Tests;

/// <summary>
/// Feature 004.1 Task 2 — <see cref="HighwayErrors"/> formatting.
/// Requirement 2 AC1/AC2: every message starts with the stable prefix
/// <c>ERR HW_</c> followed by the code and a human-readable detail.
/// These strings are a stable client contract (005 classifies on them).
/// </summary>
public class HighwayErrorsTests
{
    [Fact]
    public void InvalidArg_FormsWithPrefixAndDetail()
        => HighwayErrors.InvalidArgError("service is blank")
            .Should().Be("ERR HW_INVALID_ARG service is blank");

    [Fact]
    public void PayloadTooLarge_NamesActualAndLimit()
        => HighwayErrors.PayloadTooLargeError(2_097_152, 1_048_576)
            .Should().Be("ERR HW_PAYLOAD_TOO_LARGE 2097152 > 1048576");

    [Fact]
    public void InvalidCount_FormsWithPrefixAndDetail()
        => HighwayErrors.InvalidCountError("COUNT is not numeric")
            .Should().Be("ERR HW_INVALID_COUNT COUNT is not numeric");

    [Fact]
    public void Internal_FormsWithPrefixAndDetail()
        => HighwayErrors.InternalError("something broke")
            .Should().Be("ERR HW_INTERNAL something broke");

    [Fact]
    public void AllCodes_StartWithStablePrefix()
    {
        HighwayErrors.InvalidArgError("x").Should().StartWith(HighwayErrors.Prefix);
        HighwayErrors.PayloadTooLargeError(1, 0).Should().StartWith(HighwayErrors.Prefix);
        HighwayErrors.InvalidCountError("x").Should().StartWith(HighwayErrors.Prefix);
        HighwayErrors.InternalError("x").Should().StartWith(HighwayErrors.Prefix);
    }

    [Fact]
    public void Codes_AreStableStrings()
    {
        // Lock the contract: 005 switches on these exact values.
        HighwayErrors.InvalidArg.Should().Be("HW_INVALID_ARG");
        HighwayErrors.PayloadTooLarge.Should().Be("HW_PAYLOAD_TOO_LARGE");
        HighwayErrors.InvalidCount.Should().Be("HW_INVALID_COUNT");
        HighwayErrors.Internal.Should().Be("HW_INTERNAL");
    }
}
