namespace Highway.Assurance.Edge;

using System.Diagnostics;
using Highway.Abstractions;
using Highway.Assurance.Contracts;
using Highway.Assurance.Ledger;
using Highway.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var node = GetArg(args, "--node", "edge-1");
        var server = GetArg(args, "--server", "127.0.0.1:6500");
        var runDir = GetArg(args, "--run-dir", ".");
        var targetRate = int.TryParse(GetArg(args, "--rate", "100"), out var r) ? r : 100;

        var ledgerPath = Path.Combine(runDir, "ledgers", $"{node}.jsonl");
        var ledgerWriter = new LedgerWriter(ledgerPath, "edge", node);

        var builder = Host.CreateApplicationBuilder(args);
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; });
        builder.Logging.SetMinimumLevel(LogLevel.Information);

        builder.Services.AddSingleton(ledgerWriter);

        builder.Services.AddHighway(o =>
        {
            o.NodeName = node;
            o.Server = server;
            o.SubscriptionGroup = "edge";
        });

        builder.Services.AddHostedService(sp => new EdgeLoadGeneratorService(
            sp.GetRequiredService<IHighwayClient>(),
            ledgerWriter,
            runDir,
            targetRate,
            sp.GetRequiredService<ILogger<EdgeLoadGeneratorService>>()));

        var app = builder.Build();
        await app.RunAsync();
        await ledgerWriter.DisposeAsync();

        return 0;
    }

    private static string GetArg(string[] args, string name, string defaultValue)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }
        return defaultValue;
    }
}

public sealed class EmailDispatchedSubscriber(
    LedgerWriter ledger,
    ILogger<EmailDispatchedSubscriber> logger)
    : ISubscribe<EmailDispatched>
{
    public async Task SubscribeAsync(EmailDispatched message, CancellationToken ct = default)
    {
        await ledger.WriteAsync(new LedgerEntry
        {
            Kind = "received",
            Type = "EmailDispatched",
            Cid = message.Cid,
            Group = "edge"
        }, ct);

        logger.LogInformation("[Edge] Received EmailDispatched {Cid} for Email {EmailCid} - Cycle closed!",
            message.Cid, message.EmailCid);
    }
}

public sealed class EdgeLoadGeneratorService(
    IHighwayClient client,
    LedgerWriter ledger,
    string runDir,
    int targetRatePerSec,
    ILogger<EdgeLoadGeneratorService> logger)
    : BackgroundService
{
    private long _cidSeq;
    private string _currentPhase = "settle";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("[Edge] Starting load generator service with target rate: {Rate} msg/s", targetRatePerSec);

        var phaseFile = Path.Combine(runDir, "config", "current_phase.txt");
        var intervalMs = Math.Max(1, 1000.0 / Math.Max(1, targetRatePerSec));

        var nextOpTime = Stopwatch.GetTimestamp();

        while (!stoppingToken.IsCancellationRequested)
        {
            // Check phase
            try
            {
                if (File.Exists(phaseFile))
                {
                    using var stream = new FileStream(phaseFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(stream);
                    var phase = (await reader.ReadToEndAsync(stoppingToken).ConfigureAwait(false))?.Trim() ?? "";
                    if (!string.IsNullOrEmpty(phase) && phase != _currentPhase)
                    {
                        _currentPhase = phase;
                        await ledger.RecordPhaseAsync(phase, stoppingToken).ConfigureAwait(false);
                        logger.LogInformation("[Edge] Phase updated to {Phase}", phase);
                    }
                }
            }
            catch
            {
                // Ignore transient file sharing exceptions
            }

            // Only generate load during active load phases
            if (_currentPhase is "gap" or "arrival" or "steady" or "turbulence")
            {
                try
                {
                    await DispatchSingleOperationAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "[Edge] Operation failed: {Message}", ex.Message);
                }

                // Rate limiting delay
                await Task.Delay(TimeSpan.FromMilliseconds(intervalMs), stoppingToken).ConfigureAwait(false);
            }
            else
            {
                // In settle, drain, shutdown phases, sleep briefly
                await Task.Delay(100, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private async Task DispatchSingleOperationAsync(CancellationToken ct)
    {
        var seq = Interlocked.Increment(ref _cidSeq);
        var cid = $"edge-{seq:000000}";
        var dice = seq % 5;

        var sw = Stopwatch.StartNew();

        switch (dice)
        {
            case 0:
                // RPC 1: ValidateAccount (happy path)
                await ledger.WriteAsync(new LedgerEntry
                {
                    Kind = "executed",
                    Type = "ValidateAccount",
                    Cid = cid
                }, ct);

                try
                {
                    var res = await client.ExecuteAsync(new ValidateAccount { Cid = cid, UserId = (int)(seq % 500) + 1 }, ct);
                    sw.Stop();
                    await ledger.WriteAsync(new LedgerEntry
                    {
                        Kind = "replied",
                        Type = "ValidateAccount",
                        Cid = cid,
                        Status = res.StatusCode ?? 200,
                        Ms = sw.Elapsed.TotalMilliseconds
                    }, ct);
                }
                catch (Exception)
                {
                    await ledger.WriteAsync(new LedgerEntry
                    {
                        Kind = "timed-out",
                        Type = "ValidateAccount",
                        Cid = cid
                    }, ct);
                    throw;
                }
                break;

            case 1:
                // RPC 2: GetProfile (known absent UserId 9999 -> returns 404 as data)
                await ledger.WriteAsync(new LedgerEntry
                {
                    Kind = "executed",
                    Type = "GetProfile",
                    Cid = cid
                }, ct);

                try
                {
                    var res = await client.ExecuteAsync(new GetProfile { Cid = cid, UserId = 9999 }, ct);
                    sw.Stop();
                    await ledger.WriteAsync(new LedgerEntry
                    {
                        Kind = "replied",
                        Type = "GetProfile",
                        Cid = cid,
                        Status = res.StatusCode ?? 404,
                        Ms = sw.Elapsed.TotalMilliseconds
                    }, ct);
                }
                catch (Exception)
                {
                    await ledger.WriteAsync(new LedgerEntry
                    {
                        Kind = "timed-out",
                        Type = "GetProfile",
                        Cid = cid
                    }, ct);
                    throw;
                }
                break;

            case 2:
                // Pub/Sub 1: UserSignedUp
                await client.PublishAsync(new UserSignedUp { Cid = cid, UserId = (int)(seq % 500) + 1 }, ct);
                await ledger.WriteAsync(new LedgerEntry
                {
                    Kind = "published",
                    Type = "UserSignedUp",
                    Cid = cid
                }, ct);
                break;

            case 3:
                // Pub/Sub 2: PasswordResetRequested (accounts subscribes -> produces reset email + audits)
                await client.PublishAsync(new PasswordResetRequested { Cid = cid, UserId = (int)(seq % 500) + 1 }, ct);
                await ledger.WriteAsync(new LedgerEntry
                {
                    Kind = "published",
                    Type = "PasswordResetRequested",
                    Cid = cid
                }, ct);
                break;

            case 4:
                // Queue: SendEmail (Kind="signup")
                var msgId = await client.SendAsync(new SendEmail
                {
                    Cid = cid,
                    Kind = "signup",
                    UserId = (int)(seq % 500) + 1,
                    Body = "Welcome to Highway! Confirm your email address."
                }, ct);

                await ledger.WriteAsync(new LedgerEntry
                {
                    Kind = "sent",
                    Type = "SendEmail",
                    Cid = cid,
                    MsgId = msgId
                }, ct);
                break;
        }
    }
}
