using Highway.Server.Observability;
using Highway.Server.Storage;

namespace Highway.Server.Commands.Runtime;

/// <summary>
/// Everything a command needs that is not its arguments (039 T1): the store, the per-name
/// lock, the doorbell, the flight recorder, the options, and — crucially — the wall-clock
/// value read <b>once</b> before execution (037 R5.1: no clock inside a batch). A command
/// reads <see cref="NowTicks"/>; it never calls <c>DateTime.UtcNow</c>.
///
/// <para>Commands never see Kestrel, RocksDB, or a pipe — only this context and a
/// <see cref="CommandInput"/> / <see cref="RespWriter"/>. That is the transport seam
/// (037 R10): the same command runs under the RESP server (040) or a test harness (039).</para>
/// </summary>
internal sealed class CommandContext
{
    /// <summary>The storage seam (038).</summary>
    public IHighwayStore Store { get; }

    /// <summary>The per-name lock — a command acquires the stripe for the structure it mutates.</summary>
    public StripedLock Locks { get; }

    /// <summary>The doorbell, rung post-commit. Best-effort (037 R7).</summary>
    public IDoorbell Doorbell { get; }

    /// <summary>The flight recorder — observability, written post-commit. Never affects delivery (037 R7/C7).</summary>
    public FlightRecorder Recorder { get; }

    /// <summary>Server options (identifier/payload caps, lease, byte budget, attempt limit…).</summary>
    public HighwayServerOptions Options { get; }

    /// <summary>
    /// The wall clock, read once before the command runs. Every time value the command
    /// writes is derived from this absolute tick count — so WAL replay reproduces
    /// byte-identical state (037 R5). A command that read the clock itself would reintroduce
    /// the non-determinism the port exists to remove.
    /// </summary>
    public long NowTicks { get; }

    public CommandContext(
        IHighwayStore store,
        StripedLock locks,
        IDoorbell doorbell,
        FlightRecorder recorder,
        HighwayServerOptions options,
        long nowTicks)
    {
        Store = store;
        Locks = locks;
        Doorbell = doorbell;
        Recorder = recorder;
        Options = options;
        NowTicks = nowTicks;
    }
}
