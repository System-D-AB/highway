namespace Highway.Assurance.Accounts;

using Highway.Client;
using Highway.Assurance.Ledger;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var node = GetArg(args, "--node", "accounts-1");
        var server = GetArg(args, "--server", "127.0.0.1:6500");
        var runDir = GetArg(args, "--run-dir", ".");

        var ledgerPath = Path.Combine(runDir, "ledgers", $"{node}.jsonl");
        var ledgerWriter = new LedgerWriter(ledgerPath, "accounts", node);

        var builder = Host.CreateApplicationBuilder(args);
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; });
        builder.Logging.SetMinimumLevel(LogLevel.Information);

        builder.Services.AddSingleton(ledgerWriter);

        builder.Services.AddHighway(o =>
        {
            o.NodeName = node;
            o.Server = server;
            o.SubscriptionGroup = "accounts";
        });

        builder.Services.AddHostedService(sp => new PhaseWatcherService(runDir, ledgerWriter, sp.GetRequiredService<ILogger<PhaseWatcherService>>()));

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

public sealed class PhaseWatcherService(string runDir, LedgerWriter ledgerWriter, ILogger<PhaseWatcherService> logger) : BackgroundService
{
    private string _lastPhase = "";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var phaseFile = Path.Combine(runDir, "config", "current_phase.txt");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (File.Exists(phaseFile))
                {
                    using var stream = new FileStream(phaseFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(stream);
                    var phase = (await reader.ReadToEndAsync(stoppingToken).ConfigureAwait(false))?.Trim() ?? "";
                    if (!string.IsNullOrEmpty(phase) && phase != _lastPhase)
                    {
                        _lastPhase = phase;
                        await ledgerWriter.RecordPhaseAsync(phase, stoppingToken).ConfigureAwait(false);
                        logger.LogInformation("Phase transitioned to: {Phase}", phase);
                    }
                }
            }
            catch
            {
                // Ignore transient file reading exceptions
            }

            await Task.Delay(250, stoppingToken).ConfigureAwait(false);
        }
    }
}
