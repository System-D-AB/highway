using FluentAssertions;
using Highway.Abstractions;
using Xunit;

namespace Highway.Abstractions.Tests;

public class OutputTests
{
    private sealed class TestOutput : Output;

    [Fact]
    public void StatusCode_DefaultsToNull()
    {
        var output = new TestOutput();
        output.StatusCode.Should().BeNull();
    }

    [Fact]
    public void Error_DefaultsToNull()
    {
        var output = new TestOutput();
        output.Error.Should().BeNull();
    }
}
