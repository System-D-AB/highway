namespace Highway.Server.Storage;

/// <summary>
/// A point-in-time copy of the in-memory keyspace. Reads issued against it see the
/// keyspace as it was when <see cref="InMemoryStore.Snapshot"/> was called, regardless
/// of concurrent commits — the isolation the contract suite requires.
/// </summary>
internal sealed class InMemorySnapshot : IStoreSnapshot
{
    internal SortedDictionary<byte[], byte[]> Map { get; }

    internal InMemorySnapshot(SortedDictionary<byte[], byte[]> map) => Map = map;

    public void Dispose() { }
}
