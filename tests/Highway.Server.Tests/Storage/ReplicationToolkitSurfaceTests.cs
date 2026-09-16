using System.Buffers;
using System.Reflection;
using System.Text;
using FluentAssertions;
using RocksDbSharp;
using Xunit;

namespace Highway.Server.Tests.Storage;

/// <summary>
/// 042 T2v.1 — re-assert the pinned <c>RocksDB 11.1.2.3412</c> replication toolkit so a
/// package bump cannot silently drop the surface the feeder/applier/snapshot adapters wrap.
/// Compile-time references are the real guard; the reflection checks name the members the
/// 2026-08-29 addendum listed.
/// </summary>
public class ReplicationToolkitSurfaceTests
{
    [Fact]
    public void PinnedAssembly_IsCuriosityRocksDbSharp_11_1_2_3412()
    {
        var assembly = typeof(ReplicationSource).Assembly;
        var name = assembly.GetName();
        name.Name.Should().Be("RocksDbSharp");

        var info = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                   ?? name.Version?.ToString();
        info.Should().StartWith("11.1.2",
            "Directory.Packages.props pins RocksDB 11.1.2.3412; a bump must re-run T2v");
    }

    [Fact]
    public void ToolkitSurface_ManagedMembersExist_NoPInvokeRequired()
    {
        typeof(ReplicationSource).GetMethod(nameof(ReplicationSource.GetWalUpdates))
            .Should().NotBeNull();
        typeof(ReplicationSource).GetMethod(nameof(ReplicationSource.GetPooledWalUpdates))
            .Should().NotBeNull();
        typeof(ReplicationSource).GetMethod(nameof(ReplicationSource.GetInitialState))
            .Should().NotBeNull();

        typeof(ReplicationConsumer).GetMethod(nameof(ReplicationConsumer.IngestBatch), [typeof(ReplicationBatch)])
            .Should().NotBeNull();
        typeof(ReplicationConsumer).GetMethods()
            .Should().Contain(m => m.Name == nameof(ReplicationConsumer.IngestBatch) && m.GetParameters().Length == 2,
                "IngestBatch(ulong, ReadOnlySpan<byte>) is the zero-copy apply path");

        typeof(ReplicationSession).GetMethod(nameof(ReplicationSession.GetManifest)).Should().NotBeNull();
        typeof(ReplicationSession).GetMethod(nameof(ReplicationSession.OpenFile)).Should().NotBeNull();
        typeof(ReplicationSession).GetProperty(nameof(ReplicationSession.Files)).Should().NotBeNull();

        typeof(RocksDb).GetMethod(nameof(RocksDb.GetLatestSequenceNumber)).Should().NotBeNull();
        typeof(RocksDb).GetMethod(nameof(RocksDb.DisableFileDeletions)).Should().NotBeNull();
        typeof(RocksDb).GetMethod(nameof(RocksDb.EnableFileDeletions)).Should().NotBeNull();
        typeof(RocksDb).GetProperty(nameof(RocksDb.WalPath)).Should().NotBeNull();
        typeof(RocksDbWalInspector).Should().NotBeNull();
    }

    [Fact]
    public void RoundTrip_PrimaryPut_Pull_Ingest_ReplicaSeesBytes()
    {
        var primaryDir = NewTempDir();
        var replicaDir = NewTempDir();
        var checkpointDir = Path.Combine(Path.GetTempPath(), "hw-repl-cp-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var primary = Open(primaryDir);
            using var replica = Open(replicaDir);

            primary.DisableFileDeletions();
            try
            {
                primary.Put("k", "v1", writeOptions: Sync());

                var source = new ReplicationSource(primary);
                var pages = source.GetWalUpdates(0).ToList();
                pages.Should().NotBeEmpty("a sync Put must produce a WAL batch");

                var consumer = new ReplicationConsumer(replica);
                foreach (var page in pages)
                    consumer.IngestBatch(page.SequenceNumber, page.Data);

                Encoding.UTF8.GetString(replica.Get("k"u8.ToArray())!).Should().Be("v1");
                replica.GetLatestSequenceNumber().Should().BeGreaterThan(0);

                var pooled = source.GetPooledWalUpdates(0).ToList();
                try
                {
                    pooled.Should().NotBeEmpty();
                    pooled[0].Length.Should().BePositive();
                    pooled[0].PooledData.Should().NotBeNull();
                }
                finally
                {
                    foreach (var p in pooled)
                    {
                        if (p.PooledData is not null)
                            ArrayPool<byte>.Shared.Return(p.PooledData);
                    }
                }

                var session = source.GetInitialState(checkpointDir);
                try
                {
                    var manifest = session.GetManifest();
                    manifest.Should().NotBeEmpty("a checkpoint must list files");
                    var named = manifest[0].FileName;
                    named.Should().NotBeNullOrEmpty();
                    using (var file = session.OpenFile(named))
                    {
                        file.FileName.Should().Be(named);
                        file.FileStream.CanRead.Should().BeTrue();
                    }

                    // Files opens a stream per entry — dispose them before deleting the dir.
                    var opened = session.Files.ToList();
                    try
                    {
                        opened.Should().NotBeEmpty();
                    }
                    finally
                    {
                        foreach (var f in opened)
                            f.Dispose();
                    }
                }
                finally
                {
                    try { session.Dispose(); }
                    catch (IOException)
                    {
                        // Windows: checkpoint WAL/SST hard-links stay held by the live primary.
                    }
                }
            }
            finally
            {
                primary.EnableFileDeletions();
            }

            _ = primary.WalPath; // member exists; empty when WAL lives in the db dir
        }
        finally
        {
            TryDelete(primaryDir);
            TryDelete(replicaDir);
            TryDelete(checkpointDir);
        }
    }

    internal static RocksDb Open(string path)
        => RocksDb.Open(new DbOptions().SetCreateIfMissing(true).SetWalTtlSeconds(0), path);

    internal static WriteOptions Sync() => new WriteOptions().SetSync(true);

    internal static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hw-repl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    internal static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }
}
