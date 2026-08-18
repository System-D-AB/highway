namespace Highway.Assurance.Ledger;

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

public sealed class LedgerWriter : IAsyncDisposable, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    private readonly string _filePath;
    private readonly FileStream _fileStream;
    private readonly StreamWriter _writer;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _disposed;

    public string FilePath => _filePath;
    public string App { get; }
    public string Node { get; }
    public string CurrentPhase { get; set; } = "settle";

    public LedgerWriter(string filePath, string app, string node)
    {
        _filePath = filePath;
        App = app;
        Node = node;

        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        // Open with FileShare.ReadWrite so observers or reconciler can read concurrently
        _fileStream = new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, 4096, FileOptions.WriteThrough);
        _writer = new StreamWriter(_fileStream, new UTF8Encoding(false));
    }

    public async Task WriteAsync(LedgerEntry entry, CancellationToken ct = default)
    {
        if (_disposed) return;

        if (string.IsNullOrEmpty(entry.App)) entry.App = App;
        if (string.IsNullOrEmpty(entry.Node)) entry.Node = Node;
        if (string.IsNullOrEmpty(entry.Phase)) entry.Phase = CurrentPhase;
        if (string.IsNullOrEmpty(entry.Ts)) entry.Ts = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ");

        var json = JsonSerializer.Serialize(entry, JsonOptions);

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
    }

    public void Write(LedgerEntry entry)
    {
        if (_disposed) return;

        if (string.IsNullOrEmpty(entry.App)) entry.App = App;
        if (string.IsNullOrEmpty(entry.Node)) entry.Node = Node;
        if (string.IsNullOrEmpty(entry.Phase)) entry.Phase = CurrentPhase;
        if (string.IsNullOrEmpty(entry.Ts)) entry.Ts = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ");

        var json = JsonSerializer.Serialize(entry, JsonOptions);

        _lock.Wait();
        try
        {
            _writer.WriteLine(json);
            _writer.Flush();
            _fileStream.Flush(flushToDisk: true);
        }
        finally
        {
            _lock.Release();
        }
    }

    public Task RecordPhaseAsync(string phaseName, CancellationToken ct = default)
    {
        CurrentPhase = phaseName;
        return WriteAsync(new LedgerEntry
        {
            Kind = "phase",
            Type = phaseName
        }, ct);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _writer.Flush();
            _writer.Dispose();
            _fileStream.Dispose();
            _lock.Dispose();
        }
        catch
        {
            // Ignore errors during final flush on process shutdown
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            await _writer.FlushAsync().ConfigureAwait(false);
            await _writer.DisposeAsync().ConfigureAwait(false);
            await _fileStream.DisposeAsync().ConfigureAwait(false);
            _lock.Dispose();
        }
        catch
        {
            // Ignore errors during final flush on process shutdown
        }
    }
}
