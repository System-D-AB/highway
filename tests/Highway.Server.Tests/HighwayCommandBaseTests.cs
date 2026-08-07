using System.Buffers;
using System.Text;
using FluentAssertions;
using Garnet.common;
using Garnet.server;
using Highway.Server.Commands;
using Highway.Server.Internal;
using Xunit;

namespace Highway.Server.Tests;

/// <summary>
/// Feature 004.1 Task 3 — <see cref="HighwayCommandBase"/> error-capture
/// mechanics, tested without a live transaction (Requirement 2 AC3: validation
/// failures render as data from Main; Requirement 2 AC1: ERR HW_ prefix).
/// The TryRead* paths are covered end-to-end by ErrorContractTests (Task 8).
/// </summary>
public class HighwayCommandBaseTests
{
    /// <summary>Concrete subclass exposing the protected surface for tests.</summary>
    private sealed class TestableCommand : HighwayCommandBase
    {
        public bool ExposeFailed => Failed;
        public bool ExposeFail(string code, string detail) => Fail(code, detail);
        public bool ExposeTryWriteError(ref MemoryResult<byte> output) => TryWriteError(ref output);

        public override bool Prepare<TGarnetReadApi>(TGarnetReadApi api, ref CustomProcedureInput procInput) => true;
        public override void Main<TGarnetApi>(TGarnetApi api, ref CustomProcedureInput procInput, ref MemoryResult<byte> output) { }
    }

    private static string Render(TestableCommand command)
    {
        var output = new MemoryResult<byte>();
        try
        {
            command.ExposeTryWriteError(ref output).Should().BeTrue();
            return Encoding.ASCII.GetString(output.MemoryOwner!.Memory.Span[..output.Length]);
        }
        finally
        {
            output.Dispose();
        }
    }

    [Fact]
    public void Failed_InitiallyFalse()
        => new TestableCommand().ExposeFailed.Should().BeFalse();

    [Fact]
    public void TryWriteError_WithoutFailure_WritesNothing()
    {
        var output = new MemoryResult<byte>();
        try
        {
            new TestableCommand().ExposeTryWriteError(ref output).Should().BeFalse();
            output.Length.Should().Be(0);
        }
        finally
        {
            output.Dispose();
        }
    }

    [Fact]
    public void Fail_CapturesError_AsRespErrorFrame()
    {
        var command = new TestableCommand();
        command.ExposeFail(HighwayErrors.InvalidArg, "service is blank").Should().BeFalse();
        command.ExposeFailed.Should().BeTrue();

        Render(command).Should().Be("-ERR HW_INVALID_ARG service is blank\r\n");
    }

    [Fact]
    public void Fail_FirstFailureWins()
    {
        var command = new TestableCommand();
        command.ExposeFail(HighwayErrors.InvalidArg, "first problem");
        command.ExposeFail(HighwayErrors.PayloadTooLarge, "9999 > 1024");

        Render(command).Should().Be("-ERR HW_INVALID_ARG first problem\r\n",
            "the client must see one stable diagnosis, not the last one");
    }

    [Fact]
    public void Fail_PayloadTooLarge_MatchesDocumentedShape()
    {
        var command = new TestableCommand();
        command.ExposeFail(HighwayErrors.PayloadTooLarge, "2000000 > 1048576");

        Render(command).Should().Be("-ERR HW_PAYLOAD_TOO_LARGE 2000000 > 1048576\r\n");
    }
}
