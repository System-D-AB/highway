using System.Buffers.Binary;
using System.Text;
using Highway.Server.Storage;
using Highway.Server.Storage.Layout;
using Highway.Server.Storage.Rocks;

// =============================================================================
// Highway.Storage.CrashHarness — a child process for the T6 crash-replay test
// (038 R4.4). It opens a RocksDbStore at a given dir, commits a deterministic
// stream of queue pushes (each its own sync-per-commit batch), and prints each
// acknowledged sequence number to stdout so the parent knows what committed
// before it hard-kills the process. On reopen the parent asserts every
// acknowledged push survived and the dump is byte-identical to a clean run.
//
//   Highway.Storage.CrashHarness write --dir <path> [--count N]
//
// With --count the harness stops after N commits and exits cleanly (the
// "reference" run). Without it, it writes forever until killed (the crash run).
// =============================================================================

if (args.Length < 2 || args[0] != "write")
{
    Console.Error.WriteLine("usage: write --dir <path> [--count N]");
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

static byte[] DeterministicValue(long i)
{
    // 32 bytes: the index repeated, so equal indices give equal bytes across runs.
    var v = new byte[32];
    BinaryPrimitives.WriteInt64BigEndian(v, i);
    Encoding.ASCII.GetBytes("crash-payload-").CopyTo(v, 8);
    return v;
}
