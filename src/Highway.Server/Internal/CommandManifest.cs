namespace Highway.Server.Internal;

/// <summary>
/// Guards the seam between Highway's command registrations and Garnet's AOF.
///
/// <para><b>The problem this closes</b> (registered as finding A1 in the substrate review,
/// <c>docs/product/brainstorming.md</c>): Garnet's append-only file records custom transactions
/// by <i>registration position</i>, not by name. Replaying an AOF against a build whose
/// registration order differs does not fail — it <b>re-executes the wrong procedures</b>,
/// silently, against the durable state. Removing or reordering a command is therefore a
/// data-corrupting change that nothing in the build or tests would catch.</para>
///
/// <para>The guard: the ordered command-name list is written beside the data directory on
/// first durable start. Every later start compares before recovery runs and refuses on
/// divergence — the same refusing-beats-misparsing rule as 013's storage version byte and
/// 018's pre-unification scan.</para>
///
/// <para><b>Appending is compatible.</b> New commands added at the end leave every existing
/// position — and therefore every existing AOF record — meaning what it always meant, so a
/// stored manifest that is a strict prefix of the current table is accepted and the file is
/// extended. This is also why <c>HighwayServer.CommandTable</c> is append-only.</para>
/// </summary>
internal static class CommandManifest
{
    public const string FileName = "highway-command-manifest.txt";

    private const string Header =
        """
        # Highway command manifest.
        # The ORDER of these names defines the stored-procedure ids recorded in this data
        # directory's AOF. Do not edit: a mismatch with the running build's registration
        # order makes AOF recovery replay the WRONG procedures. See CommandTable in
        # HighwayServer.cs, which is append-only for exactly this reason.
        """;

    /// <summary>
    /// Validates (and creates or extends) the manifest for <paramref name="dataDir"/>.
    /// No-op in memory-only mode. Throws before any recovery can run when the stored order
    /// and the current registration order have diverged.
    /// </summary>
    public static void Guard(string? dataDir, IReadOnlyList<string> commandNames)
    {
        if (dataDir is null) return;

        var dir = Path.GetFullPath(dataDir);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, FileName);

        if (!File.Exists(path))
        {
            // First durable start — or a data directory from before this guard existed. The
            // pre-guard case cannot be verified retroactively; the manifest protects every
            // start from this one forward.
            Write(path, commandNames);
            return;
        }

        var stored = File.ReadAllLines(path)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .ToArray();

        if (stored.SequenceEqual(commandNames, StringComparer.Ordinal))
            return;

        // A strict prefix means commands were only APPENDED since the manifest was written:
        // every stored position still means what it meant, so the old AOF replays correctly.
        if (stored.Length < commandNames.Count
            && stored.SequenceEqual(commandNames.Take(stored.Length), StringComparer.Ordinal))
        {
            Write(path, commandNames);
            return;
        }

        var divergence = DescribeDivergence(stored, commandNames);

        throw new InvalidOperationException(
            $"The command registrations of this Highway build do not match the ones that wrote " +
            $"'{path}' ({divergence}). Garnet's AOF stores procedures by registration position, so " +
            $"recovering this data directory would replay the WRONG commands against your durable " +
            $"queues. Refusing to start rather than corrupting them. Remedies: run the Highway " +
            $"version that wrote this data directory; or, if its contents are disposable, delete " +
            $"the directory to start fresh — every queued message in it will be lost.");
    }

    private static void Write(string path, IReadOnlyList<string> commandNames)
        => File.WriteAllLines(path, [Header, .. commandNames]);

    /// <summary>Names the first position where the two orders disagree — the operator's first question.</summary>
    private static string DescribeDivergence(string[] stored, IReadOnlyList<string> current)
    {
        var shared = Math.Min(stored.Length, current.Count);
        for (var i = 0; i < shared; i++)
        {
            if (!string.Equals(stored[i], current[i], StringComparison.Ordinal))
                return $"position {i}: data directory has '{stored[i]}', this build registers '{current[i]}'";
        }

        // No positional mismatch, and prefix-extension was already accepted — so the stored
        // list must be LONGER: this build removed commands the data directory knows about.
        return $"the data directory lists {stored.Length} commands, this build registers only {current.Count} — " +
               $"'{stored[shared]}' and later were removed";
    }
}
