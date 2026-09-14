namespace Highway.Server.Storage;

/// <summary>
/// A set of staged mutations that commit together or not at all (037 R4). This is
/// how the collapsed <c>Prepare</c>/<c>Main</c> model works: a command decides
/// everything against a snapshot, stages every write here, and commits once.
///
/// <para><b>Exactly one commit point (037 R4.1).</b> <see cref="Commit"/> is the
/// single place durable state changes. A command that throws or returns before
/// calling it leaves the store untouched (037 R4.2) — proven by the fault-injection
/// test T2.4 requires.</para>
///
/// <para><b>Reads see staged writes.</b> Mutators that must observe their own earlier
/// staging within the same command — <see cref="IHighwayStore.ListLeftPop"/> after a
/// push, <see cref="IHighwayStore.Increment"/> reserving the next seq for a push in the
/// same batch — take the batch, not a snapshot, so the pop/increment reflects what is
/// already staged. Under RocksDB this is a batch with its own read view; see
/// <c>docs/features/037-rocksdb-engine/reference/stow-engine/Engine/WritePath.cs</c>,
/// which builds one <c>WriteBatch</c> per operation and commits it with
/// <c>db.Write(batch)</c>.</para>
///
/// <para><b>No clock inside (037 R5.1).</b> Nothing staged here may read the wall
/// clock. Every time value written into the batch is an absolute tick count computed
/// from the single clock read the command took before opening the batch. That is what
/// makes WAL replay after an ungraceful kill reproduce byte-identical state (037 R5.3)
/// — the batch records the <b>effect</b>, never the intent to compute one.</para>
///
/// <para><b>No engine type on it (Gate G1).</b> Under RocksDB this wraps a
/// <c>WriteBatch</c>; that handle never surfaces here.</para>
///
/// <para>Disposing without committing discards every staged mutation.</para>
/// </summary>
public interface IStoreBatch : IDisposable
{
    /// <summary>
    /// Commits every staged mutation atomically and durably. The one place in the
    /// codebase a batch is committed (037 R4.1). After this returns the writes are in
    /// the WAL; before it, nothing is visible to any other command.
    /// </summary>
    void Commit();
}
