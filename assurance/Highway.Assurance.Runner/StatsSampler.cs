namespace Highway.Assurance.Runner;

using System.Text;
using System.Text.Json;
using StackExchange.Redis;

public sealed class StatsSampler : IAsyncDisposable, IDisposable
{
    private readonly ConnectionMultiplexer _mux;
    private readonly IDatabase _db;
    private readonly string _outputFile;
    private readonly FileStream _fileStream;
    private readonly StreamWriter _writer;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _disposed;

    public StatsSampler(string serverEndpoint, string outputFile)
    {
        _outputFile = outputFile;
        var dir = Path.GetDirectoryName(outputFile);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _mux = ConnectionMultiplexer.Connect(new ConfigurationOptions
        {
            EndPoints = { serverEndpoint },
            AbortOnConnectFail = false,
            ConnectTimeout = 5000,
            SyncTimeout = 5000
        });
        _db = _mux.GetDatabase();

        _fileStream = new FileStream(outputFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        _writer = new StreamWriter(_fileStream, Encoding.UTF8);
    }

    public async Task<Dictionary<string, object>> SampleAsync(CancellationToken ct = default)
    {
        var sample = new Dictionary<string, object>
        {
            ["ts"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ")
        };

        try
        {
            // 1. Server Stats
            var serverStatsResult = await _db.ExecuteAsync("HW.STATS");
            sample["server"] = ParseFlatKvArray(serverStatsResult);

            // 2. Queue Stats for email.send
            var queueStatsResult = await _db.ExecuteAsync("HW.STATS", "Q:email.send");
            var queueStats = ParseFlatKvArray(queueStatsResult);
            sample["queues"] = new Dictionary<string, object>
            {
                ["email.send"] = queueStats
            };

            // 3. Channel Stats
            var channels = new Dictionary<string, object>();
            foreach (var ch in new[] { "users.signedup", "users.passwordreset", "accounts.audited", "email.dispatched" })
            {
                var chResult = await _db.ExecuteAsync("HW.STATS", ch);
                channels[ch] = ParseFlatKvArray(chResult);
            }
            sample["channels"] = channels;

            // 4. Discover nodes for known services
            var discover = new Dictionary<string, List<string>>();
            foreach (var svc in new[] { "ValidateAccount", "GetProfile" })
            {
                var discoverResult = await _db.ExecuteAsync("HW.DISCOVER", svc);
                discover[svc] = ParseDiscover(discoverResult);
            }
            sample["discover"] = discover;
        }
        catch (Exception ex)
        {
            sample["error"] = ex.Message;
        }

        var json = JsonSerializer.Serialize(sample);
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _writer.WriteLineAsync(json.AsMemory(), ct).ConfigureAwait(false);
            await _writer.FlushAsync(ct).ConfigureAwait(false);
            await _fileStream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }

        return sample;
    }

    public async Task CaptureDlqAsync(string dlqOutputFile, CancellationToken ct = default)
    {
        try
        {
            var res = await _db.ExecuteAsync("HW.DLQ", "PEEK", "Q:email.send");
            var entries = new List<Dictionary<string, string>>();
            if (res != null && res.Resp2Type == ResultType.Array)
            {
                var raw = (RedisResult[])res!;
                for (var i = 0; i < raw.Length; i++)
                {
                    entries.Add(new Dictionary<string, string> { ["entry"] = raw[i].ToString() ?? "" });
                }
            }
            var dir = Path.GetDirectoryName(dlqOutputFile);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(dlqOutputFile, JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true }), ct).ConfigureAwait(false);
        }
        catch
        {
            // If DLQ is empty or command returns nil
            var dir = Path.GetDirectoryName(dlqOutputFile);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(dlqOutputFile, "[]", ct).ConfigureAwait(false);
        }
    }

    public async Task CaptureFlightRecorderReplayAsync(string replayOutputFile, CancellationToken ct = default)
    {
        try
        {
            var lines = new List<string>();
            var targets = new[]
            {
                "email.send",
                "users.signedup",
                "users.passwordreset",
                "accounts.audited",
                "email.dispatched",
                "ValidateAccount",
                "GetProfile"
            };

            foreach (var target in targets)
            {
                try
                {
                    var res = await _db.ExecuteAsync("HW.REPLAY", target, "LIMIT", "1000");
                    if (res != null && res.Resp2Type == ResultType.Array)
                    {
                        var raw = (RedisResult[])res!;
                        foreach (var item in raw)
                        {
                            var s = item.ToString();
                            if (!string.IsNullOrEmpty(s))
                            {
                                lines.Add($"{target}: {s}");
                            }
                        }
                    }
                }
                catch { }
            }

            var dir = Path.GetDirectoryName(replayOutputFile);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            await File.WriteAllLinesAsync(replayOutputFile, lines, ct).ConfigureAwait(false);
        }
        catch
        {
            // If flight recorder is empty or unpopulated
            var dir = Path.GetDirectoryName(replayOutputFile);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(replayOutputFile, "", ct).ConfigureAwait(false);
        }
    }

    public static Dictionary<string, object> ParseFlatKvArray(RedisResult result)
    {
        var dict = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        if (result != null && result.Resp2Type == ResultType.Array)
        {
            var items = (RedisResult[])result!;
            for (var i = 0; i < items.Length - 1; i += 2)
            {
                var key = items[i].ToString() ?? "";
                var valStr = items[i + 1].ToString() ?? "";
                if (int.TryParse(valStr, out var intVal)) dict[key] = intVal;
                else if (long.TryParse(valStr, out var longVal)) dict[key] = longVal;
                else dict[key] = valStr;
            }
        }
        return dict;
    }

    public static List<string> ParseDiscover(RedisResult result)
    {
        var list = new List<string>();
        if (result != null && result.Resp2Type == ResultType.Array)
        {
            var items = (RedisResult[])result!;
            foreach (var item in items)
            {
                list.Add(item.ToString() ?? "");
            }
        }
        return list;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _writer.Dispose();
        _fileStream.Dispose();
        _mux.Dispose();
        _lock.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _writer.DisposeAsync().ConfigureAwait(false);
        await _fileStream.DisposeAsync().ConfigureAwait(false);
        _mux.Dispose();
        _lock.Dispose();
    }
}
