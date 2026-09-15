namespace Highway.Server.Commands.Runtime;

/// <summary>
/// The arguments a command receives — the engine-free replacement for Garnet's
/// <c>CustomProcedureInput</c> / <c>SessionParseState</c> (039 T1). Args arrive as an
/// ordered list of byte slices (one per RESP bulk string after the command name), read
/// positionally exactly as <c>GetNextArg</c> did: index-by-position, and an out-of-range
/// read yields an empty slice that commands treat as "absent".
///
/// <para>040 (the RESP server) constructs this from a parsed frame; 039's tests construct
/// it directly from <c>byte[][]</c> — the transport-seam proof (037 R10) that a command
/// runs with no socket.</para>
/// </summary>
internal sealed class CommandInput
{
    private readonly IReadOnlyList<byte[]> _args;

    /// <summary>Builds an input from the argument slices (excluding the command name).</summary>
    public CommandInput(IReadOnlyList<byte[]> args) => _args = args;

    /// <summary>Convenience for tests: build from UTF-8 strings.</summary>
    public static CommandInput FromStrings(params string[] args)
        => new([.. args.Select(System.Text.Encoding.UTF8.GetBytes)]);

    /// <summary>Number of arguments.</summary>
    public int Count => _args.Count;

    /// <summary>
    /// Reads the argument at <paramref name="idx"/> and advances it. Returns an empty
    /// span when past the end — the "absent optional argument" signal commands rely on
    /// (<c>if (arg.Length > 0)</c>).
    /// </summary>
    public ReadOnlySpan<byte> Next(ref int idx)
    {
        var arg = idx < _args.Count ? _args[idx].AsSpan() : ReadOnlySpan<byte>.Empty;
        idx++;
        return arg;
    }
}
