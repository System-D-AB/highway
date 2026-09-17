namespace Highway.Client.Engine;

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Highway.Client.Wire;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
    private string _activeServer;
    private bool _disposed;

    private readonly string _originalServer;
    private readonly ILogger _logger;
    private int _warnedSingleEndpoint;   // 050 T6: warn about a single-endpoint replica set at most once

    /// <summary>Identifies the master a client has adopted, with the epoch it was last seen at (050 T5).</summary>
    public readonly record struct MasterChange(string Endpoint, ulong Epoch);

    /// <summary>
    /// Raised when this client adopts a new master — a failover walk, a <c>-NOTPRIMARY</c> redirect,
    /// or the initial connect settling on a different endpoint (050 R7.4). The application can
    /// subscribe (the source is a DI singleton) to react — circuit-break, alert, drop a cache — and
    /// every change is also logged. Never fires when the endpoint is unchanged.
    /// </summary>
    public event Action<MasterChange>? MasterChanged;

    public HighwayConnectionSource(IHighwayConnectionSettings settings, ILoggerFactory? loggerFactory = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<HighwayConnectionSource>();
        _originalServer = settings.Server ?? "";

        // 042 R6.1: the connection string may name several hosts
        // ("host1:6500,host2:6500,password=…"). The active connection always targets ONE
        // of them; the full list is kept for failover when the active host dies.
        var hosts = HostsOf(_originalServer);
        _activeServer = hosts.Length > 1
            ? hosts[0] + OptionsOf(_originalServer)
            : _originalServer;
    }

    public IHighwayConnectionSettings Settings => _settings;
    internal string ActiveServer => _activeServer;

    /// <summary>Every host the connection string names, in order.</summary>
    internal string[] ConfiguredEndpoints => HostsOf(_originalServer);

    /// <summary>True when the connection string names more than one host (042 R6.1).</summary>
    public bool HasAlternateEndpoints => HostsOf(_originalServer).Length > 1;

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
                var server = _activeServer;
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
            _ = RefreshRosterAsync();   // learn the live roster (042-1b B-T1); advisory
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
            // 042-1b B-R1: the bootstrap list's promise is "reach SOME node". When the
            // first endpoint is a corpse, the walk tries the rest (a willing successor
            // may need a moment, hence the short retry window).
            if (HasAlternateEndpoints)
            {
                for (var attempt = 0; attempt < 8; attempt++)
                {
                    if (await TryFailoverAsync().ConfigureAwait(false) && _multiplexer is { } adopted)
                        return adopted;
                    await Task.Delay(250).ConfigureAwait(false);
                }
            }

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

    /// <summary>
    /// 042 T6: move the multiplexer to the endpoint advertised by <c>-NOTPRIMARY</c>,
    /// keeping password/TLS options from the original connection string.
    /// </summary>
    public void SwitchTo(string endpoint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        var next = endpoint + OptionsOf(_originalServer);
        IConnectionMultiplexer? old;
        string previousHost;
        lock (_syncLock)
        {
            previousHost = HostOf(_activeServer);
            if (string.Equals(previousHost, endpoint, StringComparison.OrdinalIgnoreCase)
                && _multiplexer is { IsConnected: true })
                return;

            old = _multiplexer;
            _multiplexer = null;
            _connectTask = null;
            _activeServer = next;
        }

        DisposeLater(old);
        RaiseMasterChanged(previousHost, endpoint);
        _ = GetMultiplexerAsync();
    }

    private readonly SemaphoreSlim _failoverGate = new(1, 1);

    // ---- the live roster (042-1b B-T1 / parent R13.2) ------------------------

    /// <summary>One roster row as the client sees it.</summary>
    internal readonly record struct RosterEntry(string NodeId, int Priority, string Endpoint);

    private IReadOnlyList<RosterEntry>? _roster;
    private ulong _rosterVersion;
    private ulong _lastSeenEpoch;

    /// <summary>The cached live roster (empty until learned); the running truth for successor order.</summary>
    internal IReadOnlyList<RosterEntry> Roster => _roster ?? [];

    /// <summary>
    /// Test seam (042-1d partition matrix): endpoints this predicate rejects are treated
    /// as unreachable by the walk — client-side partition simulation, declared rather
    /// than smuggled. Null = everything reachable.
    /// </summary>
    internal Func<string, bool>? EndpointReachableOverride { get; set; }

    /// <summary>Test seam: seed the roster without a wire read.</summary>
    internal void SetRosterForTests(IReadOnlyList<RosterEntry> roster, ulong version)
    {
        lock (_syncLock)
        {
            _roster = roster;
            _rosterVersion = version;
        }
    }

    internal ulong RosterVersion => _rosterVersion;

    /// <summary>The highest epoch this client has observed (handshakes, -NOTPRIMARY). Never invented.</summary>
    internal ulong LastSeenEpoch => _lastSeenEpoch;

    internal void NoteObservedEpoch(ulong epoch)
    {
        while (true)
        {
            var seen = Volatile.Read(ref _lastSeenEpoch);
            if (epoch <= seen) return;
            if (Interlocked.CompareExchange(ref _lastSeenEpoch, epoch, seen) == seen) return;
        }
    }

    /// <summary>Logs and raises <see cref="MasterChanged"/> — only when the master host actually changed (050 T5).</summary>
    private void RaiseMasterChanged(string? previousHost, string newHost)
    {
        if (string.Equals(previousHost, newHost, StringComparison.OrdinalIgnoreCase)) return;
        var epoch = Volatile.Read(ref _lastSeenEpoch);
        _logger.LogInformation("Highway master changed to {Master} (epoch {Epoch})", newHost, epoch);
        MasterChanged?.Invoke(new MasterChange(newHost, epoch));
    }

    /// <summary>
    /// 050 T6 (F5): the single-endpoint footgun. When the broker is part of a replica set but this
    /// client's connection string names only one host, the client has no failover target — warn once,
    /// naming the endpoints it is missing. Internal so the check is unit-testable without a wire read.
    /// </summary>
    internal void MaybeWarnSingleEndpoint(int rosterMemberCount)
    {
        if (rosterMemberCount <= 1 || ConfiguredEndpoints.Length != 1) return;
        if (Interlocked.Exchange(ref _warnedSingleEndpoint, 1) != 0) return;

        var configured = HostOf(_activeServer);
        var missing = (_roster ?? [])
            .Select(e => e.Endpoint)
            .Where(ep => !string.Equals(ep, configured, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        _logger.LogWarning(
            "Highway replica set has {Count} members but the connection string names only one endpoint — " +
            "this client has NO failover. Add the other endpoints to the connection string: {Missing}",
            rosterMemberCount, string.Join(", ", missing));
    }

    /// <summary>
    /// Reads the live roster off the current connection (`HW.REPL.STATUS` roster.* fields).
    /// Best-effort: a broker without replication, or a transient failure, leaves the cache
    /// as it was — the bootstrap list remains the fallback.
    /// </summary>
    public async Task RefreshRosterAsync(CancellationToken ct = default)
    {
        try
        {
            var mux = _multiplexer;
            if (mux is null || !mux.IsConnected) return;

            var status = (RedisResult[])(await mux.GetDatabase().ExecuteAsync("HW.REPL.STATUS").ConfigureAwait(false))!;
            ApplyRosterFields(status);
        }
        catch
        {
            // Advisory refresh; the successor rule falls back to what it has.
        }
    }

    private void ApplyRosterFields(RedisResult[] flat)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i + 1 < flat.Length; i += 2)
            fields[flat[i].ToString()!] = flat[i + 1].ToString()!;

        if (!fields.TryGetValue("roster.version", out var versionText)
            || !ulong.TryParse(versionText, out var version) || version == 0)
            return;

        var entries = new List<RosterEntry>();
        for (var n = 0; ; n++)
        {
            if (!fields.TryGetValue($"roster.{n}.id", out var id)) break;
            var priority = int.Parse(fields[$"roster.{n}.priority"]);
            entries.Add(new RosterEntry(id, priority, fields[$"roster.{n}.endpoint"]));
        }

        lock (_syncLock)
        {
            if (version >= _rosterVersion)
            {
                _rosterVersion = version;
                _roster = entries;
            }
        }

        MaybeWarnSingleEndpoint(entries.Count);   // 050 T6: the single-endpoint footgun, once
    }

    /// <summary>
    /// The walk's candidate endpoints, in order: the live roster by ascending priority
    /// (skipping priority-0 never-promote nodes), else the bootstrap list. The current
    /// (just-failed or departing) host goes last.
    /// </summary>
    internal string[] CandidateEndpoints(bool excludeCurrent = false)
    {
        var current = HostOf(_activeServer);
        var fromRoster = (_roster ?? [])
            .Where(e => e.Priority != 0)
            .OrderBy(e => e.Priority)
            .Select(e => e.Endpoint)
            .ToArray();

        var list = (fromRoster.Length > 0 ? fromRoster : HostsOf(_originalServer))
            .Where(h => !excludeCurrent || !string.Equals(h, current, StringComparison.OrdinalIgnoreCase))
            .OrderBy(h => string.Equals(h, current, StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return list;
    }

    /// <summary>
    /// The herd walk (042-1b B-T2 / parent R3): candidates in roster priority order, the
    /// <c>HW.REPL.HELLO CLIENT</c> handshake at each — <c>master</c>/<c>willing</c> is
    /// adopted (the willing standby promotes on our first verb, 042-1c); a
    /// <c>standby</c>'s redirect is probed next in preference to walking blind; a broker
    /// without replication (<c>HW_INVALID_ARG</c>) is a plain writable node and adopted.
    /// Returns false when nothing answers; the caller's bounded retry then runs out
    /// exactly as before.
    ///
    /// <para><b>Serialized.</b> Every in-flight operation hits connection loss at once;
    /// one sweep runs, the rest wait on the gate and inherit the adopted multiplexer.
    /// <paramref name="excludeCurrent"/> is the GOODBYE shape: the incumbent said go, so
    /// it is not a candidate even though it still answers.</para>
    /// </summary>
    public async Task<bool> TryFailoverAsync(CancellationToken ct = default, bool excludeCurrent = false)
    {
        await _failoverGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // A concurrent sweep may already have adopted a live endpoint (not good
            // enough when the incumbent is saying GOODBYE — then we must move).
            if (!excludeCurrent && _multiplexer is { IsConnected: true })
                return true;

            var candidates = new List<string>(CandidateEndpoints(excludeCurrent));
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            while (candidates.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var host = candidates[0];
                candidates.RemoveAt(0);
                if (!visited.Add(host))
                    continue;

                if (EndpointReachableOverride is { } reachable && !reachable(host))
                    continue;   // simulated partition (test seam)

                IConnectionMultiplexer? probe = null;
                try
                {
                    var options = HighwayConnectionConfiguration.Build(host + OptionsOf(_originalServer), _settings);
                    options.ConnectTimeout = Math.Min(options.ConnectTimeout, 2_000);
                    options.AbortOnConnectFail = true;
                    probe = await ConnectionMultiplexer.ConnectAsync(options).ConfigureAwait(false);

                    var verdict = await HandshakeAsync(probe).ConfigureAwait(false);
                    if (verdict.Adopt)
                    {
                        IConnectionMultiplexer? old;
                        string previousHost;
                        lock (_syncLock)
                        {
                            previousHost = HostOf(_activeServer);
                            old = _multiplexer;
                            _multiplexer = probe;
                            _connectTask = Task.FromResult(probe);
                            _activeServer = host + OptionsOf(_originalServer);
                        }
                        DisposeLater(old);
                        RaiseMasterChanged(previousHost, host);
                        _ = RefreshRosterAsync(ct);
                        return true;
                    }

                    probe.Dispose();
                    if (verdict.Redirect is { } redirect && !visited.Contains(redirect))
                        candidates.Insert(0, redirect);   // the standby pointed at its master — go there next
                }
                catch
                {
                    probe?.Dispose();
                }
            }

            return false;
        }
        finally
        {
            _failoverGate.Release();
        }
    }

    private readonly record struct HandshakeVerdict(bool Adopt, string? Redirect);

    private async Task<HandshakeVerdict> HandshakeAsync(IConnectionMultiplexer probe)
    {
        try
        {
            var clientId = _settings is HighwayOptions { NodeName.Length: > 0 } o ? o.NodeName : "client";
            var reply = (RedisResult[])(await probe.GetDatabase().ExecuteAsync(
                "HW.REPL.HELLO", "CLIENT", clientId,
                LastSeenEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .ConfigureAwait(false))!;

            var role = reply[0].ToString();
            if (role is "master" or "willing")
            {
                if (ulong.TryParse(reply[1].ToString(), out var epoch))
                    NoteObservedEpoch(epoch);
                return new HandshakeVerdict(Adopt: true, Redirect: null);
            }

            // "standby": follow its redirect (the master it still sees).
            if (ulong.TryParse(reply[2].ToString(), out var masterEpoch))
                NoteObservedEpoch(masterEpoch);
            return new HandshakeVerdict(Adopt: false, Redirect: reply[1].ToString());
        }
        catch (RedisServerException ex) when (ex.Message.Contains("HW_INVALID_ARG", StringComparison.Ordinal))
        {
            return new HandshakeVerdict(Adopt: true, Redirect: null);   // plain broker, no replication
        }
    }

    /// <summary>
    /// Disposes a replaced multiplexer after a grace window, so operations already
    /// holding it finish (or fail into the bounded retry) instead of hitting
    /// ObjectDisposed mid-flight (042 G6).
    /// </summary>
    private static void DisposeLater(IConnectionMultiplexer? old)
    {
        if (old is null) return;
        _ = Task.Delay(TimeSpan.FromSeconds(5)).ContinueWith(
            _ => { try { old.Dispose(); } catch { /* already gone */ } },
            TaskScheduler.Default);
    }

    /// <summary>The leading comma-segments that are hosts (no '='); the rest are options.</summary>
    internal static string[] HostsOf(string server)
    {
        if (string.IsNullOrWhiteSpace(server)) return [];
        return server.Split(',')
            .TakeWhile(s => !s.Contains('='))
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToArray();
    }

    /// <summary>The option tail (",password=…" etc.), empty when the string is hosts only.</summary>
    internal static string OptionsOf(string server)
    {
        var segments = server.Split(',');
        var firstOption = Array.FindIndex(segments, s => s.Contains('='));
        return firstOption < 0 ? "" : "," + string.Join(',', segments[firstOption..]);
    }

    internal static string HostOf(string server)
    {
        var hosts = HostsOf(server);
        return hosts.Length > 0 ? hosts[0] : server;
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
