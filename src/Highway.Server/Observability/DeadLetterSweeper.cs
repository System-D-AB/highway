using System.Collections.Concurrent;
using Highway.Server.Internal;
using Highway.Server.Storage;
using Highway.Server.Storage.Boxes;
using Microsoft.Extensions.Logging;

namespace Highway.Server.Observability;

/// <summary>
/// Periodically inspects dead-letter backlogs and logs a broker warning when
/// messages have aged beyond <see cref="HighwayServerOptions.DeadLetterAgeWarning"/>
/// without any active dead-letter feed subscription (feature 073 R9 AC3).
/// </summary>
internal sealed class DeadLetterSweeper : IDisposable
{
    private readonly IHighwayStore _store;
    private readonly HighwayServerOptions _options;
    private readonly Func<ILogger?> _getLogger;
    private readonly Timer? _timer;
    private readonly ConcurrentDictionary<string, bool> _warned = new(StringComparer.Ordinal);
    private int _running;

    public DeadLetterSweeper(
        IHighwayStore store,
        HighwayServerOptions options,
        Func<ILogger?> getLogger,
        TimeSpan? interval = null)
    {
        _store = store;
        _options = options;
        _getLogger = getLogger;

        if (options.DeadLetterAgeWarning > TimeSpan.Zero)
        {
            var checkInterval = interval ?? TimeSpan.FromMinutes(1);
            _timer = new Timer(OnTick, null, checkInterval, checkInterval);
        }
    }

    private void OnTick(object? _)
    {
        if (Interlocked.Exchange(ref _running, 1) == 1) return;
        try { Sweep(); }
        catch { /* best effort: monitoring never fails the broker */ }
        finally { Interlocked.Exchange(ref _running, 0); }
    }

    /// <summary>
    /// Checks all queue, service, and channel boxes for aged unconsumed dead letters.
    /// Can be invoked directly by tests.
    /// </summary>
    public void Sweep()
    {
        var logger = _getLogger();
        var rt = new BoxRuntime();
        using var batch = _store.NewBatch();
        var kinds = new[] { BoxKeys.Kind.Queue, BoxKeys.Kind.Service };
        var nowTicks = DateTime.UtcNow.Ticks;
        var ageWarningTicks = _options.DeadLetterAgeWarning.Ticks;

        foreach (var kind in kinds)
        {
            foreach (var (_, name, id) in Catalog.List(_store, batch, kind))
            {
                using var wb = new WorkBox(_store, rt, id, tracksBytes: false, batch);
                var targetKey = $"{kind}:{name}";

                if (wb.Dead == 0 || wb.FeedOn > 0)
                {
                    _warned.TryRemove(targetKey, out _);
                    continue;
                }

                if (_warned.ContainsKey(targetKey))
                    continue;

                var oldest = wb.PeekDead(1);
                if (oldest.Count == 0) continue;

                DeadLetter.Decode(oldest[0].Value, out var ticks, out _, out _, out _);
                if (nowTicks - ticks >= ageWarningTicks)
                {
                    if (_warned.TryAdd(targetKey, true))
                    {
                        logger?.LogWarning(
                            "[dead-letters] target '{Target}' has {Count} dead letter(s) unconsumed for longer than {AgeWarning} with no declared feed (a list nobody reads)",
                            name, wb.Dead, _options.DeadLetterAgeWarning);
                    }
                }
            }
        }
    }

    public void Dispose() => _timer?.Dispose();
}
