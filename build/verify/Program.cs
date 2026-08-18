using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Highway.Abstractions;
using Highway.Client;
using Highway.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

Console.WriteLine("=== Highway Package Verification Consumer ===");

// 1. Assert Highway.Abstractions has zero package dependencies
var currentDir = new DirectoryInfo(AppContext.BaseDirectory);
while (currentDir != null && !Directory.Exists(Path.Combine(currentDir.FullName, "artifacts", "packages")))
{
    currentDir = currentDir.Parent;
}
if (currentDir == null)
{
    throw new DirectoryNotFoundException("Could not find repository root containing artifacts/packages");
}
var nupkgPath = Path.Combine(currentDir.FullName, "artifacts", "packages", "Highway.Abstractions.1.0.0-preview.1.nupkg");
if (!File.Exists(nupkgPath))
{
    throw new FileNotFoundException($"Package not found at {nupkgPath}");
}

using (var zip = ZipFile.OpenRead(nupkgPath))
{
    var nuspecEntry = zip.GetEntry("Highway.Abstractions.nuspec") ?? throw new InvalidOperationException("Nuspec not found in package");
    using var stream = nuspecEntry.Open();
    var doc = XDocument.Load(stream);
    var ns = doc.Root?.GetDefaultNamespace() ?? XNamespace.None;
    var depElements = doc.Descendants(ns + "dependency").ToList();
    if (depElements.Count != 0)
    {
        throw new InvalidOperationException($"Highway.Abstractions MUST have 0 package dependencies, found {depElements.Count}!");
    }
    Console.WriteLine("[PASS] Highway.Abstractions has 0 package dependencies.");
}

// 2. Start HighwayTestServer from Highway.Server package
Console.WriteLine("Starting in-process HighwayTestServer...");
using var server = new HighwayTestServer();
Console.WriteLine($"HighwayTestServer listening on port {server.Port}");

// 3. Setup client host from Highway.Client package
var services = new ServiceCollection();
services.AddLogging();
services.AddHighway(options =>
{
    options.Server = server.ConnectionString;
    options.NodeName = "verify-consumer-node";
});
services.AddSingleton<VerifyProcessor>();

await using var sp = services.BuildServiceProvider();
var hostedServices = sp.GetServices<IHostedService>();
foreach (var hs in hostedServices)
{
    await hs.StartAsync(CancellationToken.None);
}

var client = sp.GetRequiredService<IHighwayClient>();

// 4. Send and process a message
VerifyProcessor.Reset();
var testId = Guid.NewGuid().ToString("N");
Console.WriteLine($"Sending VerifyMessage {testId} to queue...");
await client.SendAsync(new VerifyMessage { TestId = testId, Payload = "Package verification payload" });

var received = await VerifyProcessor.WaitForMessageAsync(TimeSpan.FromSeconds(10));
if (received?.TestId != testId)
{
    throw new InvalidOperationException($"Failed to receive expected message. Expected: {testId}, Got: {received?.TestId ?? "null"}");
}

Console.WriteLine($"[PASS] Successfully roundtripped message {testId} through packaged client and server!");

foreach (var hs in hostedServices)
{
    await hs.StopAsync(CancellationToken.None);
}
Console.WriteLine("=== All Consumer Verifications PASSED ===");
return 0;

[Queue("verify.queue")]
public sealed class VerifyMessage : ISend
{
    public string TestId { get; set; } = "";
    public string Payload { get; set; } = "";
}

public sealed class VerifyProcessor : IProcess<VerifyMessage>
{
    private static TaskCompletionSource<VerifyMessage> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static void Reset()
    {
        _tcs = new TaskCompletionSource<VerifyMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public Task ProcessAsync(VerifyMessage message, CancellationToken ct = default)
    {
        Console.WriteLine($"[Processor] Received message {message.TestId}: {message.Payload}");
        _tcs.TrySetResult(message);
        return Task.CompletedTask;
    }

    public static Task<VerifyMessage> WaitForMessageAsync(TimeSpan timeout)
        => _tcs.Task.WaitAsync(timeout);
}
