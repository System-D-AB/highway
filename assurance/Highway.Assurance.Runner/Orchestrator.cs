namespace Highway.Assurance.Runner;

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using Highway.Assurance.Reconciler;
using Highway.Assurance.Reconciler.Models;
using Highway.Assurance.Runner.Models;
using StackExchange.Redis;

public sealed class Orchestrator
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public async Task<ReconciliationResult> ExecuteRunAsync(string runDir, RunProfile profile, CancellationToken ct = default)
    {
        Console.WriteLine($"===============================================================================");
        Console.WriteLine($"[Runner] Starting Assurance Rig Run: {profile.Name} at {DateTime.UtcNow:u}");
        Console.WriteLine($"[Runner] Target Rate: {profile.TargetRatePerSec} msg/s | Lease: {profile.LeaseSeconds}s");
        Console.WriteLine($"[Runner] Run Directory: {runDir}");
        Console.WriteLine($"===============================================================================");

        // 1. Directory Layout
        var configDir = Path.Combine(runDir, "config");
        var ledgersDir = Path.Combine(runDir, "ledgers");
        var brokerDir = Path.Combine(runDir, "broker");
        var procDir = Path.Combine(runDir, "processes");
        var dataDir = Path.Combine(runDir, "broker-data");

        Directory.CreateDirectory(configDir);
        Directory.CreateDirectory(ledgersDir);
        Directory.CreateDirectory(brokerDir);
        Directory.CreateDirectory(procDir);
        Directory.CreateDirectory(dataDir);

        // 2. Ephemeral Port
        var port = GetFreeTcpPort();
        var serverEndpoint = $"127.0.0.1:{port}";
        Console.WriteLine($"[Runner] Broker endpoint: {serverEndpoint}");

        // 3. Generate highway.json
        var highwayConfig = new
        {
            server = new
            {
                port = port,
                bindAddress = "127.0.0.1",
                dataDir = dataDir,
                lease = $"00:00:{profile.LeaseSeconds:00}",
                observability = new
                {
                    recorderEnabled = true
                }
            }
        };
        var configPath = Path.Combine(configDir, "highway.json");
        await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(highwayConfig, JsonOptions), ct).ConfigureAwait(false);

        // Write profile.json & versions.json
        await File.WriteAllTextAsync(Path.Combine(configDir, "profile.json"), JsonSerializer.Serialize(profile, JsonOptions), ct).ConfigureAwait(false);
        var versions = new
        {
            gitSha = GetGitSha(),
            timestamp = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ"),
            dotnetVersion = Environment.Version.ToString(),
            os = Environment.OSVersion.ToString()
        };
        await File.WriteAllTextAsync(Path.Combine(runDir, "versions.json"), JsonSerializer.Serialize(versions, JsonOptions), ct).ConfigureAwait(false);

        var currentPhaseFile = Path.Combine(configDir, "current_phase.txt");
        await SetPhaseAsync(currentPhaseFile, "settle", ct).ConfigureAwait(false);

        // Initial storage measurement
        var storageBefore = GetDirectorySizeBytes(dataDir);

        await using var procManager = new ProcessManager(runDir);

        // 4. Locate binaries
        var hostAssembly = FindAssemblyPath("highways.dll") ?? FindAssemblyPath("Highway.Server.Host.dll");
        var edgeAssembly = FindAssemblyPath("Highway.Assurance.Edge.dll");
        var accountsAssembly = FindAssemblyPath("Highway.Assurance.Accounts.dll");
        var notifsAssembly = FindAssemblyPath("Highway.Assurance.Notifications.dll");

        if (hostAssembly == null || edgeAssembly == null || accountsAssembly == null || notifsAssembly == null)
        {
            throw new FileNotFoundException($"Failed to locate required binaries. Found: broker={hostAssembly}, edge={edgeAssembly}, accounts={accountsAssembly}, notifs={notifsAssembly}");
        }

        // 5. Start Broker
        Console.WriteLine($"[Runner] Starting highways broker process...");
        var brokerProc = procManager.StartDotnetAssembly("broker", hostAssembly, $"--config \"{configPath}\"");

        // Wait for RESP PING
        await WaitForBrokerReadyAsync(serverEndpoint, TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
        Console.WriteLine($"[Runner] Broker is healthy and answering PING.");

        await using var sampler = new StatsSampler(serverEndpoint, Path.Combine(brokerDir, "stats-samples.jsonl"));
        using var sampleCts = new CancellationTokenSource();
        var samplingTask = StartPeriodicSamplingAsync(sampler, TimeSpan.FromMilliseconds(500), sampleCts.Token);

        try
        {
            // 6. Settle Phase (0..settleSeconds)
            Console.WriteLine($"[Runner] Phase: SETTLE (starting edge-1, accounts-1, notifications-subs-1)");
            procManager.StartDotnetAssembly("edge-1", edgeAssembly, $"--node edge-1 --server {serverEndpoint} --run-dir \"{runDir}\" --rate {profile.TargetRatePerSec}");
            procManager.StartDotnetAssembly("accounts-1", accountsAssembly, $"--node accounts-1 --server {serverEndpoint} --run-dir \"{runDir}\"");
            procManager.StartDotnetAssembly("notifications-subs-1", notifsAssembly, $"--node notifications-subs-1 --server {serverEndpoint} --run-dir \"{runDir}\" --role subs");

            await WaitForSettleConditionAsync(sampler, TimeSpan.FromSeconds(profile.SettleSeconds), ct).ConfigureAwait(false);
            Console.WriteLine($"[Runner] Settle complete — all nodes visible and groups registered.");

            // 7. Gap Phase
            Console.WriteLine($"[Runner] Phase: GAP ({profile.GapSeconds}s) — Producers active, zero mailers running");
            await SetPhaseAsync(currentPhaseFile, "gap", ct).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromSeconds(profile.GapSeconds), ct).ConfigureAwait(false);

            // 8. Arrival Phase
            Console.WriteLine($"[Runner] Phase: ARRIVAL ({profile.ArrivalSeconds}s) — Starting mailer-1 and mailer-2");
            await SetPhaseAsync(currentPhaseFile, "arrival", ct).ConfigureAwait(false);
            procManager.StartDotnetAssembly("mailer-1", notifsAssembly, $"--node mailer-1 --server {serverEndpoint} --run-dir \"{runDir}\" --role mailer");
            procManager.StartDotnetAssembly("mailer-2", notifsAssembly, $"--node mailer-2 --server {serverEndpoint} --run-dir \"{runDir}\" --role mailer");
            await Task.Delay(TimeSpan.FromSeconds(profile.ArrivalSeconds), ct).ConfigureAwait(false);

            // 9. Steady Phase
            Console.WriteLine($"[Runner] Phase: STEADY ({profile.SteadySeconds}s) — Full load, all components active");
            await SetPhaseAsync(currentPhaseFile, "steady", ct).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromSeconds(profile.SteadySeconds), ct).ConfigureAwait(false);

            // 10. Turbulence Phase
            Console.WriteLine($"[Runner] Phase: TURBULENCE ({profile.TurbulenceSeconds}s) — Graceful restart & ungraceful kill scheduled");
            await SetPhaseAsync(currentPhaseFile, "turbulence", ct).ConfigureAwait(false);

            var turbSw = Stopwatch.StartNew();

            // Wait until restart offset
            var restartDelay = TimeSpan.FromSeconds(profile.SubscriberGracefulRestartOffsetSeconds) - turbSw.Elapsed;
            if (restartDelay > TimeSpan.Zero) await Task.Delay(restartDelay, ct).ConfigureAwait(false);

            Console.WriteLine($"[Runner] t+{profile.SubscriberGracefulRestartOffsetSeconds}s: Restarting notifications-subs-1 gracefully...");
            var subProc = procManager.GetProcess("notifications-subs-1");
            if (subProc != null) await subProc.StopGracefullyAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            procManager.StartDotnetAssembly("notifications-subs-1", notifsAssembly, $"--node notifications-subs-1 --server {serverEndpoint} --run-dir \"{runDir}\" --role subs");
            Console.WriteLine($"[Runner] notifications-subs-1 restarted with same node and group identity.");

            // Wait until kill offset
            var killDelay = TimeSpan.FromSeconds(profile.MailerUngracefulKillOffsetSeconds) - turbSw.Elapsed;
            if (killDelay > TimeSpan.Zero) await Task.Delay(killDelay, ct).ConfigureAwait(false);

            Console.WriteLine($"[Runner] t+{profile.MailerUngracefulKillOffsetSeconds}s: Ungracefully killing mailer-2 mid-flight...");
            var mailer2Proc = procManager.GetProcess("mailer-2");
            mailer2Proc?.KillUngracefully();
            Console.WriteLine($"[Runner] mailer-2 killed ungracefully. Surviving mailer-1 handles remaining load & lease redeliveries.");

            // Remaining turbulence duration
            var remainingTurb = TimeSpan.FromSeconds(profile.TurbulenceSeconds) - turbSw.Elapsed;
            if (remainingTurb > TimeSpan.Zero) await Task.Delay(remainingTurb, ct).ConfigureAwait(false);

            // 11. Drain Phase
            Console.WriteLine($"[Runner] Phase: DRAIN ({profile.DrainSeconds}s) — Stopping edge-1 load origin");
            await SetPhaseAsync(currentPhaseFile, "drain", ct).ConfigureAwait(false);
            var edgeProc = procManager.GetProcess("edge-1");
            if (edgeProc != null)
            {
                await edgeProc.StopGracefullyAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            }

            // Allow all outstanding messages to process during drain duration
            await Task.Delay(TimeSpan.FromSeconds(profile.DrainSeconds), ct).ConfigureAwait(false);

            // Wait for any remaining queue or channel backlog to drain completely
            await WaitForQueueDrainAsync(sampler, TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
            Console.WriteLine($"[Runner] Queue depth reached 0.");

            // 12. Shutdown Phase
            Console.WriteLine($"[Runner] Phase: SHUTDOWN");
            await SetPhaseAsync(currentPhaseFile, "shutdown", ct).ConfigureAwait(false);
            var accProc = procManager.GetProcess("accounts-1");
            if (accProc != null) await accProc.StopGracefullyAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            var subProcFinal = procManager.GetProcess("notifications-subs-1");
            if (subProcFinal != null) await subProcFinal.StopGracefullyAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            var mailer1Proc = procManager.GetProcess("mailer-1");
            if (mailer1Proc != null) await mailer1Proc.StopGracefullyAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

            // 13. Collect Broker Artifacts
            Console.WriteLine($"[Runner] Capturing DLQ and Flight Recorder dump...");
            await sampler.CaptureDlqAsync(Path.Combine(brokerDir, "dlq.json"), ct).ConfigureAwait(false);
            await sampler.CaptureFlightRecorderReplayAsync(Path.Combine(brokerDir, "recorder-replay.jsonl"), ct).ConfigureAwait(false);

            // Final sample
            await sampler.SampleAsync(ct).ConfigureAwait(false);

            // Stop sampling loop
            sampleCts.Cancel();
            try { await samplingTask.ConfigureAwait(false); } catch { }
            await sampler.DisposeAsync().ConfigureAwait(false);

            // Stop Broker
            Console.WriteLine($"[Runner] Stopping broker gracefully...");
            await brokerProc.StopGracefullyAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

            // Storage and memory metrics
            var storageAfter = GetDirectorySizeBytes(dataDir);
            var storageJson = new
            {
                dataDirSizeBytesBefore = storageBefore,
                dataDirSizeBytesAfter = storageAfter,
                dataDirSizeMb = (storageAfter / (1024.0 * 1024.0)).ToString("0.00") + " MB"
            };
            await File.WriteAllTextAsync(Path.Combine(brokerDir, "storage.json"), JsonSerializer.Serialize(storageJson, JsonOptions), ct).ConfigureAwait(false);
            procManager.RecordPeakResources(Path.Combine(procDir, "resources.json"));

            // 14. Reconcile
            Console.WriteLine($"[Runner] Phase: RECONCILE — Running set-based invariant engine...");
            var reconciler = new ReconcilerEngine();
            var result = await reconciler.ReconcileRunDirectoryAsync(runDir, ct).ConfigureAwait(false);

            Console.WriteLine($"[Runner] Verdict: {result.Verdict} (Exit Code: {result.ExitCode})");
            foreach (var (name, inv) in result.Invariants)
            {
                var icon = inv.Passed ? "✓" : "✗";
                Console.WriteLine($"  {icon} {name,-26}: {inv.Verdict,-16} | {inv.Notes}");
            }

            // Append to assurance/RUNLOG.md
            await AppendToRunLogAsync(runDir, result, profile, ct).ConfigureAwait(false);

            // Cleanup or preserve (D10)
            if (result.Verdict == "PASSED")
            {
                Console.WriteLine($"[Runner] Run PASSED — cleaning broker data directory: {dataDir}");
                try { Directory.Delete(dataDir, true); } catch { }
            }
            else
            {
                Console.WriteLine($"[Runner] Run NOT passed ({result.Verdict}) — preserving full state at: {runDir}");
            }

            return result;
        }
        finally
        {
            sampleCts.Cancel();
            await procManager.StopAllGracefullyAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }
    }

    private static async Task SetPhaseAsync(string currentPhaseFile, string phase, CancellationToken ct)
    {
        await File.WriteAllTextAsync(currentPhaseFile, phase, ct).ConfigureAwait(false);
    }

    private static async Task WaitForBrokerReadyAsync(string serverEndpoint, TimeSpan timeout, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var parts = serverEndpoint.Split(':');
        var host = parts[0];
        var port = int.Parse(parts[1]);

        while (sw.Elapsed < timeout && !ct.IsCancellationRequested)
        {
            try
            {
                using var tcp = new TcpClient();
                var connectTask = tcp.ConnectAsync(host, port, ct).AsTask();
                await Task.WhenAny(connectTask, Task.Delay(500, ct)).ConfigureAwait(false);

                if (tcp.Connected)
                {
                    using var mux = await ConnectionMultiplexer.ConnectAsync(serverEndpoint).ConfigureAwait(false);
                    var ping = await mux.GetDatabase().PingAsync().ConfigureAwait(false);
                    if (ping.TotalMilliseconds >= 0) return;
                }
            }
            catch { }
            await Task.Delay(200, ct).ConfigureAwait(false);
        }
        throw new TimeoutException($"Broker at {serverEndpoint} failed to answer PING within {timeout.TotalSeconds}s.");
    }

    private static async Task WaitForSettleConditionAsync(StatsSampler sampler, TimeSpan timeout, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout && !ct.IsCancellationRequested)
        {
            var sample = await sampler.SampleAsync(ct).ConfigureAwait(false);
            if (sample.TryGetValue("discover", out var discObj) && discObj is List<string> nodes && nodes.Count >= 3)
            {
                // Nodes visible
                return;
            }
            await Task.Delay(500, ct).ConfigureAwait(false);
        }
    }

    private static async Task WaitForQueueDrainAsync(StatsSampler sampler, TimeSpan timeout, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var consecutiveDrained = 0;
        while (sw.Elapsed < timeout && !ct.IsCancellationRequested)
        {
            var sample = await sampler.SampleAsync(ct).ConfigureAwait(false);
            var drained = true;

            if (sample.TryGetValue("queues", out var qObj) && qObj is Dictionary<string, object> queues)
            {
                if (queues.TryGetValue("email.send", out var emailQObj) && emailQObj is Dictionary<string, object> emailQ)
                {
                    var depth = emailQ.TryGetValue("depth", out var depthVal) ? Convert.ToInt32(depthVal) : 0;
                    var inFlight = emailQ.TryGetValue("inFlight", out var ifVal) ? Convert.ToInt32(ifVal) : 0;
                    if (depth > 0 || inFlight > 0) drained = false;
                }
            }

            if (sample.TryGetValue("channels", out var chObj) && chObj is Dictionary<string, object> channels)
            {
                foreach (var (_, chStatsObj) in channels)
                {
                    if (chStatsObj is Dictionary<string, object> chStats)
                    {
                        var pending = chStats.TryGetValue("pending", out var pVal) ? Convert.ToInt32(pVal) : 0;
                        if (pending > 0) drained = false;
                    }
                }
            }

            if (drained)
            {
                consecutiveDrained++;
                if (consecutiveDrained >= 3)
                {
                    // Grace period for active slow handlers to complete execution and flush ledgers
                    await Task.Delay(1000, ct).ConfigureAwait(false);
                    return;
                }
            }
            else
            {
                consecutiveDrained = 0;
            }

            await Task.Delay(500, ct).ConfigureAwait(false);
        }
    }

    private static async Task StartPeriodicSamplingAsync(StatsSampler sampler, TimeSpan interval, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await sampler.SampleAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch { }

            try
            {
                await Task.Delay(interval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
        }
    }

    private static async Task AppendToRunLogAsync(string runDir, ReconciliationResult result, RunProfile profile, CancellationToken ct)
    {
        var runLogPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "assurance", "RUNLOG.md");
        var fullRunLogPath = Path.GetFullPath(runLogPath);
        var dir = Path.GetDirectoryName(fullRunLogPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

        var sb = new System.Text.StringBuilder();
        if (!File.Exists(fullRunLogPath))
        {
            sb.AppendLine("# Assurance Rig Run Log");
            sb.AppendLine();
            sb.AppendLine("Run history recorded newest first in the house pattern.");
            sb.AppendLine();
        }

        var entryText = $"""
        ## {DateTime.UtcNow:yyyy-MM-dd} — {profile.Name} ({result.Verdict})

        - **Run ID:** `{result.RunId}`
        - **Target Rate:** {profile.TargetRatePerSec} msg/s | **Lease:** {profile.LeaseSeconds}s
        - **Verdict:** `{result.Verdict}` (Exit Code: {result.ExitCode})
        - **Total Events Processed:** {result.TotalEventsByKind.GetValueOrDefault("processed")}
        - **Duplicates Observed:** {result.Invariants.GetValueOrDefault("I5_Duplicates")?.DuplicateCount ?? 0}
        - **Dead Letters:** {result.Invariants.GetValueOrDefault("I6_DeadLetters")?.ProcessedCount ?? 0}
        - **Notes:** {string.Join("; ", result.Invariants.Values.Select(i => $"{i.Name}: {i.Verdict}"))}

        """;

        if (File.Exists(fullRunLogPath))
        {
            var existing = await File.ReadAllTextAsync(fullRunLogPath, ct).ConfigureAwait(false);
            // Insert newest first after header
            var insertPos = existing.IndexOf("## ", StringComparison.Ordinal);
            if (insertPos >= 0)
            {
                var updated = existing.Insert(insertPos, entryText + "\n");
                await File.WriteAllTextAsync(fullRunLogPath, updated, ct).ConfigureAwait(false);
                return;
            }
        }

        sb.AppendLine(entryText);
        await File.AppendAllTextAsync(fullRunLogPath, sb.ToString(), ct).ConfigureAwait(false);
    }

    private static int GetFreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string? FindAssemblyPath(string assemblyName)
    {
        // 1. Direct check in AppContext.BaseDirectory
        var direct = Path.Combine(AppContext.BaseDirectory, assemblyName);
        if (File.Exists(direct)) return direct;

        // 2. Find repo root by climbing up looking for Highway.slnx or Directory.Build.props
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        string? repoRoot = null;
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Highway.slnx")) ||
                File.Exists(Path.Combine(current.FullName, "Directory.Build.props")))
            {
                repoRoot = current.FullName;
                break;
            }
            current = current.Parent;
        }

        if (repoRoot == null)
        {
            current = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (current != null)
            {
                if (File.Exists(Path.Combine(current.FullName, "Highway.slnx")) ||
                    File.Exists(Path.Combine(current.FullName, "Directory.Build.props")))
                {
                    repoRoot = current.FullName;
                    break;
                }
                current = current.Parent;
            }
        }

        if (repoRoot != null)
        {
            var candidates = new[]
            {
                Path.Combine(repoRoot, "src", "Highway.Server.Host", "bin", "Debug", "net10.0", assemblyName),
                Path.Combine(repoRoot, "assurance", "Highway.Assurance.Edge", "bin", "Debug", "net10.0", assemblyName),
                Path.Combine(repoRoot, "assurance", "Highway.Assurance.Accounts", "bin", "Debug", "net10.0", assemblyName),
                Path.Combine(repoRoot, "assurance", "Highway.Assurance.Notifications", "bin", "Debug", "net10.0", assemblyName),
                Path.Combine(repoRoot, "src", "Highway.Server.Host", "bin", "Release", "net10.0", assemblyName),
                Path.Combine(repoRoot, "assurance", "Highway.Assurance.Edge", "bin", "Release", "net10.0", assemblyName),
                Path.Combine(repoRoot, "assurance", "Highway.Assurance.Accounts", "bin", "Release", "net10.0", assemblyName),
                Path.Combine(repoRoot, "assurance", "Highway.Assurance.Notifications", "bin", "Release", "net10.0", assemblyName)
            };

            foreach (var cand in candidates)
            {
                if (File.Exists(cand)) return cand;
            }
        }

        return null;
    }

    private static long GetDirectorySizeBytes(string dir)
    {
        if (!Directory.Exists(dir)) return 0;
        try
        {
            return Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
                .Sum(f => new FileInfo(f).Length);
        }
        catch { return 0; }
    }

    private static string GetGitSha()
    {
        try
        {
            var psi = new ProcessStartInfo("git", "rev-parse --short HEAD")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p != null)
            {
                var sha = p.StandardOutput.ReadToEnd().Trim();
                p.WaitForExit();
                return sha;
            }
        }
        catch { }
        return "unknown";
    }
}
