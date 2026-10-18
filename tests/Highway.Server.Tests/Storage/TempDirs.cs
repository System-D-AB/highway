namespace Highway.Server.Tests.Storage;

/// <summary>Temporary data directories for tests that open a durable store.</summary>
internal static class TempDirs
{
    internal static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hw-repl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// A directory holding a store whose replicated database exists, as a standby's does once it has bootstrapped from
    /// its primary (076: a standby holding none of its primary's data is never willing to take the herd).
    /// </summary>
    internal static string NewBootstrappedDir()
    {
        var dir = NewTempDir();
        Highway.Server.Storage.Crystal.CrystalHighwayStore.Open(dir).Dispose();
        return dir;
    }

    internal static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }
}
