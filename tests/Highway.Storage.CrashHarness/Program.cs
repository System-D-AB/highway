using System.Buffers.Binary;
using System.Text;
using Highway.Server.Storage;
using Highway.Server.Storage.Layout;
using Highway.Server.Storage.Rocks;
using RocksDbSharp;

// =============================================================================
// Highway.Storage.CrashHarness — a child process for crash-replay tests.
//
//   write --dir <path> [--count N]
//     038 T6 / R4.4: open a RocksDbStore, commit a deterministic stream of
//     queue pushes, print each acked sequence. With --count, stop after N
//     commits (the reference run). Without it, write forever until killed.
//
//   repl-ingest --primary <path> --replica <path>
//     042 T2v: write one sync counter on the primary, ingest onto the replica
//     with ReplicationApply (derived watermark, sync write), print
//     INGESTED <seq>, then hang until the parent hard-kills. No extra
//     watermark Put — the crash is between ingest and anything else.
// =============================================================================

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: write --dir <path> [--count N]");
    Console.Error.WriteLine("       repl-ingest --primary <path> --replica <path>");
    return 2;
}

if (args[0] == "repl-ingest")
    return RunReplIngest(args);

if (args.Length < 2 || args[0] != "write")
{
    Console.Error.WriteLine("usage: write --dir <path> [--count N]");
    Console.Error.WriteLine("       repl-ingest --primary <path> --replica <path>");
    return 2;
}

string? dir = null;
int? count = null;
for (var i = 1; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--dir": dir = args[++i]; break;
        case "--count": count = int.Parse(args[++i]); break;
    }
}

if (dir is null)
{
    Console.Error.WriteLine("--dir is required");
    return 2;
}

const string queue = "crash-queue";
var listName = HighwayNames.Queue(queue);
var counterKey = HighwayKeyspace.Counter(HighwayNames.ListSequence(listName));

using var store = RocksDbStore.Open(dir);

var stdout = Console.Out;
stdout.WriteLine("READY");
stdout.Flush();

for (long i = 0; count is null || i < count; i++)
{
    // A deterministic value so a reference run and a crash run produce identical bytes
    // for the same sequence number.
    var value = DeterministicValue(i);

    using (var batch = store.NewBatch())
    {
        var seq = store.Increment(batch, counterKey, 1) - 1; // 0-based tail seq
        store.ListRightPush(batch, HighwayKeyspace.ListEntry(listName, seq), value);
        batch.Commit(); // sync-per-commit: on disk before we ack
    }

    stdout.WriteLine(i); // ack — the parent knows commit i is durable
    stdout.Flush();
}

return 0;

static int RunReplIngest(string[] args)
{
    string? primaryDir = null;
    string? replicaDir = null;
    string? pagePath = null;
    for (var i = 1; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--primary": primaryDir = args[++i]; break;
            case "--replica": replicaDir = args[++i]; break;
            case "--page": pagePath = args[++i]; break;
        }
    }

    if (primaryDir is null || replicaDir is null)
    {
        Console.Error.WriteLine("--primary and --replica are required");
        return 2;
    }

    Directory.CreateDirectory(primaryDir);
    Directory.CreateDirectory(replicaDir);

    var options = new DbOptions().SetCreateIfMissing(true).SetWalTtlSeconds(0);
    var sync = new WriteOptions().SetSync(true);
    var counterKey = "counter"u8.ToArray();
    var one = new byte[8];
    BinaryPrimitives.WriteInt64BigEndian(one, 1);

    using var primary = RocksDb.Open(options, primaryDir);
    primary.Put(counterKey, one, writeOptions: sync);

    var pages = new ReplicationSource(primary).GetWalUpdates(0).ToList();
    if (pages.Count != 1)
    {
        Console.Error.WriteLine($"expected 1 WAL page, got {pages.Count}");
        return 1;
    }

    using var replica = RocksDb.Open(options, replicaDir);
    if (!ReplicationApply.TryIngest(replica, pages[0].SequenceNumber, pages[0].Data))
    {
        Console.Error.WriteLine("ingest skipped unexpectedly");
        return 1;
    }

    if (pagePath is not null)
        File.WriteAllBytes(pagePath, pages[0].Data);

    Console.Out.WriteLine("INGESTED " + ReplicationApply.Watermark(replica));
    Console.Out.Flush();

    // Hang until the parent hard-kills — no extra watermark write, no Dispose flush.
    Thread.Sleep(Timeout.Infinite);
    return 0;
}

static byte[] DeterministicValue(long i)
{
    // 32 bytes: the index repeated, so equal indices give equal bytes across runs.
    var v = new byte[32];
    BinaryPrimitives.WriteInt64BigEndian(v, i);
    Encoding.ASCII.GetBytes("crash-payload-").CopyTo(v, 8);
    return v;
}
