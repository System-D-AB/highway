using FluentAssertions;
using Xunit;

namespace Highway.Client.Hosting.Tests;

public class SkeletonTests
{
    [Fact]
    public void Package_skeleton_builds()
    {
        true.Should().BeTrue();
    }
}
