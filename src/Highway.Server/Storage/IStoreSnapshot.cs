namespace Highway.Server.Storage;

/// <summary>
/// A point-in-time read view of the store (037 R4). A command opens one, reads
/// everything it needs against it so a concurrent write cannot change the answer
/// mid-command, and disposes it before returning.
///
/// <para><b>No engine type on it (Gate G1).</b> Under RocksDB this wraps a
/// <c>Snapshot</c> + a <c>ReadOptions.SetSnapshot(...)</c>; that handle stays behind
/// the implementation. See <c>docs/features/037-rocksdb-engine/reference/stow-engine/Engine/ReadPath.cs</c>
/// for the create/read/dispose discipline and <c>SequenceSource.cs</c> for reading
/// the snapshot's exact sequence when a caller needs it.</para>
///
/// <para>Reads (<see cref="IHighwayStore.Get"/>, <see cref="IHighwayStore.ListLength"/>,
/// <see cref="IHighwayStore.SortedSetRangeByScore"/>, <see cref="IHighwayStore.SetMembers"/>,
/// …) take a snapshot. Mutations take an <see cref="IStoreBatch"/> instead — a
/// snapshot is read-only by construction.</para>
/// </summary>
public interface IStoreSnapshot : IDisposable
{
}
