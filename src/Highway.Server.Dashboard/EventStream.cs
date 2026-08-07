using System.Threading.Channels;
using Highway.Abstractions.Observability;
using static Highway.Server.Observability.FlightRecorder;

namespace Highway.Server.Dashboard;

/// <summary>
/// One SSE subscriber. Receives events non-blockingly via a bounded channel.
/// When the channel is full, events are dropped and counted.
/// </summary>
internal sealed class EventStream : IRecorderObserver, IAsyncDisposable
{
    private readonly string _name;
    private readonly Channel<HighwayEvent> _channel;
    private long _dropped;

    public EventStream(string name, int capacity)
    {
        _name = name;
        _channel = Channel.CreateBounded<HighwayEvent>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait, // TryWrite returns false when full — we never actually Wait
            SingleReader = true,
            SingleWriter = false,
        });
    }

    public long Dropped => Interlocked.Read(ref _dropped);
    public ChannelReader<HighwayEvent> Reader => _channel.Reader;

    public void OnRecorded(in HighwayEvent evt)
    {
        if (!string.Equals(evt.Name, _name, StringComparison.OrdinalIgnoreCase))
            return;

        if (!_channel.Writer.TryWrite(evt))
            Interlocked.Increment(ref _dropped);
    }

    public ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
