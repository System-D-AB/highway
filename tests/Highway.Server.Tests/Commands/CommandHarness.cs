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

    /// <summary>
    /// Runs <paramref name="command"/> with the given string args at wall-clock
    /// <paramref name="nowTicks"/> (default: a fixed test clock) and returns the raw RESP
    /// reply bytes.
    /// </summary>
    public byte[] Run(HighwayCommand command, long nowTicks, params string[] args)
    {
        var ctx = new CommandContext(_store, _locks, _doorbell, _recorder, _options, nowTicks);
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
