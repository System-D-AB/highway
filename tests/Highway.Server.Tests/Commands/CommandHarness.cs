using System.Collections.Generic;
using System.Text;
using Highway.Server;
using Highway.Server.Commands.Runtime;
using Highway.Server.Observability;
using Highway.Server.Storage;

namespace Highway.Server.Tests.Commands;

/// <summary>
/// Runs a ported command in-process against <see cref="InMemoryStore"/> — no socket, no
/// broker (039 T1/R4.2, the transport-seam proof). Holds one store + lock across a test so
/// multiple commands share state the way a session would.
/// </summary>
internal sealed class CommandHarness : IDisposable
{
    private readonly IHighwayStore _store;
    private readonly StripedLock _locks = new();
    private readonly RecordingDoorbell _doorbell = new();
    private readonly FlightRecorder _recorder;
    private readonly HighwayServerOptions _options;
    private readonly Highway.Server.Storage.Boxes.BoxRuntime _boxes = new();   // one per server, as the dispatcher holds

    public CommandHarness(IHighwayStore? store = null, HighwayServerOptions? options = null)
    {
        _store = store ?? new InMemoryStore();
        _options = options ?? new HighwayServerOptions();
        _recorder = new FlightRecorder(_options.Observability);
    }

    public IHighwayStore Store => _store;
    public RecordingDoorbell Doorbell => _doorbell;
    public FlightRecorder Recorder => _recorder;
    public HighwayServerOptions Options => _options;
    public StripedLock Locks => _locks;
    public Highway.Server.Storage.Boxes.BoxRuntime Boxes => _boxes;

    /// <summary>The live process view handed to commands (072); null unless a test sets one.</summary>
    public Highway.Server.Resp.InstanceRegistry? Instances { get; set; }

    /// <summary>Duplicate memories handed to commands (074); null unless a test sets them.</summary>
    public Highway.Server.Commands.Runtime.RecentIds<byte>? Calls { get; set; }
    public Highway.Server.Commands.Runtime.RecentIds<byte>? Sends { get; set; }
    public Highway.Server.Commands.Runtime.RecentIds<long>? Publishes { get; set; }

    /// <summary>
    /// Runs <paramref name="command"/> with the given string args at wall-clock
    /// <paramref name="nowTicks"/> (default: a fixed test clock) and returns the raw RESP
    /// reply bytes.
    /// </summary>
    public byte[] Run(HighwayCommand command, long nowTicks, params string[] args)
    {
        var replication = _store is Highway.Server.Storage.Rocks.RocksDbStore rocks
            ? rocks.Replication
            : null;
        var ctx = new CommandContext(_store, _locks, _doorbell, _recorder, _options, nowTicks, replication, boxes: _boxes, instances: Instances, calls: Calls, sends: Sends, publishes: Publishes);
        var input = CommandInput.FromStrings(args);
        var writer = new RespWriter();
        command.Execute(ctx, input, writer);
        return writer.ToArray();
    }

    /// <summary>
    /// Runs at a realistic default clock (real UTC now) — for tests that don't pin time.
    /// Real-scale so recorder timestamps (stamped with UtcNow) fall inside HW.REPLAY's window
    /// and DateTimeOffset arithmetic stays representable.
    /// </summary>
    public byte[] Run(HighwayCommand command, params string[] args) => Run(command, DateTime.UtcNow.Ticks, args);

    public void Dispose() { _store.Dispose(); _locks.Dispose(); }

    // -- helpers to read RESP replies as text for assertions --

    public static string AsText(byte[] resp) => Encoding.UTF8.GetString(resp);

    /// <summary>A parsed 5.0 claim row.</summary>
    public readonly record struct Claim(string Token, string Id, string Payload);

    /// <summary>
    /// Parses an HW.CLAIM reply — <c>*N</c> rows of <c>*3 [$token $id $payload]</c>, or <c>*0</c> — into its rows.
    /// </summary>
    private static readonly string CrLf = new(['\r', '\n']);

    public static List<Claim> Claims(byte[] resp)
    {
        var text = AsText(resp);
        var lines = text.Split(CrLf);
        var rows = new List<Claim>();
        if (!lines[0].StartsWith('*') || lines[0] == "*0" || lines[0] == "*-1") return rows;
        var i = 1;
        while (i < lines.Length && lines[i].StartsWith("*3"))
        {
            rows.Add(new Claim(lines[i + 2], lines[i + 4], lines[i + 6]));
            i += 7;
        }
        return rows;
    }

    /// <summary>Runs HW.CLAIM and returns its single row, or null when nothing was due.</summary>
    public Claim? ClaimOne(string kind, string name, string node, long? nowTicks = null)
    {
        var rows = Claims(Run(new Highway.Server.Commands.Ported.HwClaimCommand(), nowTicks ?? DateTime.UtcNow.Ticks, kind, name, node));
        return rows.Count == 0 ? null : rows[0];
    }
}

/// <summary>A doorbell that records rings instead of publishing — proves what a command would wake.</summary>
internal sealed class RecordingDoorbell : IDoorbell
{
    public List<(string Channel, byte[] Payload)> Rings { get; } = [];

    public int Ring(string channel, ReadOnlySpan<byte> payload)
    {
        Rings.Add((channel, payload.ToArray()));
        return 0; // best-effort; nobody connected in tests
    }
}
