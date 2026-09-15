using RocksDbSharp;

namespace Highway.Server.Storage.Rocks;

/// <summary>
/// Wraps a RocksDB <see cref="Snapshot"/> and the <see cref="ReadOptions"/> that pin
/// reads to it — a consistent point-in-time view (037 R4). Created and disposed within a
/// command; holding one across commands would pin compaction (the reference caution).
/// The RocksDB handle never surfaces past this wrapper, keeping the seam engine-free (G1).
/// </summary>
internal sealed class RocksDbSnapshot : IStoreSnapshot
{
    private readonly Snapshot _snapshot;

    internal ReadOptions ReadOptions { get; }

    internal RocksDbSnapshot(Snapshot snapshot, ReadOptions readOptions)
    {
        _snapshot = snapshot;
        ReadOptions = readOptions;
    }

    public void Dispose() => _snapshot.Dispose();
}
