# Highway.Client.Hosting

One-line host and service verbs (`install`, `uninstall`, `start`, `stop`, `status`)
for any .NET app — Windows services and systemd units.

## Quick Start

```csharp
// Program.cs — plain app (no Highway)
return await HighwayHost.RunAsync(args, null,
    b => b.Services.AddHostedService<MyWorker>());

// Program.cs — Highway app
return await HighwayHost.RunAsync(args, o => o.Server = "localhost:6379");
```

## Service Verbs

```
myapp install [--name X] [--display-name Y] [--description Z] [--user U] [--start] [-- args…]
myapp uninstall [--name X]
myapp start [--name X]
myapp stop [--name X]
myapp status [--name X]
```

Windows requires elevation (Administrator). Linux requires root and systemd.
