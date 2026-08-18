namespace Highway.Client.Engine;

using System;
using System.Threading;
using System.Threading.Tasks;
using Highway.Client.Wire;
using StackExchange.Redis;

/// <summary>
/// Singleton manager owning the single <see cref="IConnectionMultiplexer"/> per process.
/// </summary>
public sealed class HighwayConnectionSource : IAsyncDisposable, IDisposable
{
    private readonly IHighwayConnectionSettings _settings;
    private readonly object _syncLock = new();
    private Task<IConnectionMultiplexer>? _connectTask;
    private IConnectionMultiplexer? _multiplexer;
    private bool _disposed;

    public HighwayConnectionSource(IHighwayConnectionSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public IHighwayConnectionSettings Settings => _settings;

    public IConnectionMultiplexer Multiplexer
    {
        get
        {
            if (_multiplexer is not null) return _multiplexer;
            return GetMultiplexerAsync().GetAwaiter().GetResult();
        }
    }

    public IDatabase GetDatabase() => Multiplexer.GetDatabase();

    public async ValueTask<IDatabase> GetDatabaseAsync(CancellationToken ct = default)
    {
        var mux = await GetMultiplexerAsync(ct).ConfigureAwait(false);
        return mux.GetDatabase();
    }

    public async ValueTask<IConnectionMultiplexer> GetMultiplexerAsync(CancellationToken ct = default)
    {
        if (_multiplexer is not null)
            return _multiplexer;

        Task<IConnectionMultiplexer> task;
        lock (_syncLock)
        {
            if (_multiplexer is not null)
                return _multiplexer;

            if (_connectTask is null)
            {
                var server = _settings.Server;
                if (string.IsNullOrWhiteSpace(server))
                    throw new InvalidOperationException("Highway server connection string is required.");

                var options = HighwayConnectionConfiguration.Build(server, _settings);
                _connectTask = ConnectAsyncCore(options);
            }
            task = _connectTask;
        }

        return await task.ConfigureAwait(false);
    }

    private async Task<IConnectionMultiplexer> ConnectAsyncCore(ConfigurationOptions options)
    {
        try
        {
            var mux = await ConnectionMultiplexer.ConnectAsync(options).ConfigureAwait(false);
            _multiplexer = mux;
            return mux;
        }
        catch (RedisConnectionException ex) when (IsAuthenticationFailure(ex))
        {
            lock (_syncLock) { _connectTask = null; }
            throw new HighwayAuthenticationException(
                $"The Highway server at '{ConnectionStringRedactor.Redact(_settings.Server)}' rejected the supplied " +
                "credentials. Check the password, and that the server was started with WithPassword.", ex);
        }
        catch (RedisConnectionException ex)
        {
            lock (_syncLock) { _connectTask = null; }
            throw new HighwayServerUnreachableException(ConnectionStringRedactor.Redact(_settings.Server), ex);
        }
        catch
        {
            lock (_syncLock) { _connectTask = null; }
            throw;
        }
    }

    private static bool IsAuthenticationFailure(Exception? ex)
    {
        for (var cur = ex; cur is not null; cur = cur.InnerException)
        {
            var msg = cur.Message;
            if (msg.Contains("NOAUTH", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("WRONGPASS", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("Authentication failure", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("Authentication failed", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _multiplexer?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        if (_multiplexer is not null)
        {
            await _multiplexer.DisposeAsync().ConfigureAwait(false);
        }
    }
}
