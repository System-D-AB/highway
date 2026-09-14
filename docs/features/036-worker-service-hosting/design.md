# Design: Service Hosting (Feature 036)

Small package, two jobs: a one-line host, and five service verbs. Windows verb code is
031's `WindowsServiceManager`, moved out of `Highway.Server.Host` and parameterized;
Linux is a unit file plus `systemctl`. No protocol changes, no broker involvement.

## Package

```
src/Highway.Client.Hosting/
├── HighwayHost.cs              RunAsync (3 overloads)
├── HostingOptions.cs           ServiceName / DisplayName / Description / ShutdownTimeout
├── VerbParser.cs               first-arg verb, options, `--` passthrough
├── ServiceIdentity.cs          convention → code → verb-option resolution + validation
├── ExitCodes.cs                031's register, values preserved
├── WindowsServiceManager.cs    moved from Highway.Server.Host (031)
├── SystemdServiceManager.cs    unit write + systemctl (root-checked)
└── SystemdUnit.cs              deterministic unit renderer
```

Dependencies: `Microsoft.Extensions.Hosting` + `.WindowsServices` + `.Systemd`, and
`Highway.Client` (for the optional `AddHighway` wiring). `Highway.Server.Host`
references this package and deletes its private SCM copy.

## API

```csharp
public static class HighwayHost
{
    public static Task<int> RunAsync(string[] args, CancellationToken ct = default);
    public static Task<int> RunAsync(string[] args, Action<HighwayOptions>? highway,
        CancellationToken ct = default);
    public static Task<int> RunAsync(string[] args, Action<HighwayOptions>? highway,
        Action<HostApplicationBuilder> configure, CancellationToken ct = default);
}
```

```csharp
// Program.cs — plain app
return await HighwayHost.RunAsync(args, null,
    b => b.Services.AddHostedService<MyService>());

// Program.cs — Highway app
return await HighwayHost.RunAsync(args, o => o.ConnectionString = "...");
```

## Flow

```
RunAsync(args)
 ├─ first arg ∈ {install, uninstall, start, stop, status}?
 │    yes → resolve identity → Windows: WindowsServiceManager
 │                             Linux+systemd: SystemdServiceManager
 │                             else: platform-unsupported sentence
 │          exit with shared code — no host is built
 └─ no  → CreateApplicationBuilder
           ContentRoot = AppContext.BaseDirectory
           UseWindowsService() + UseSystemd()      (no-op off-platform)
           highway != null → AddHighway + engine hosted service
           configure?.Invoke(builder)
           host.RunAsync → exit code
```

## Verbs

```
myapp install [--name X] [--display-name Y] [--description Z] [--user U] [--start] [-- args…]
myapp uninstall|start|stop|status [--name X]
```

- Windows: elevation check first; then the 031 SCM calls (create with auto-start +
  description + restart-on-failure, delete, start, stop, query). The server's old
  `--config` special case becomes a passthrough arg so its behavior is unchanged.
- Linux: root check, systemd presence check; install renders the unit
  (`Type=notify` — `UseSystemd()` does sd_notify readiness — absolute `ExecStart`,
  apphost vs `dotnet app.dll` detected from `Environment.ProcessPath`), writes it,
  `daemon-reload`, `enable [--now]`. `systemctl` runs with argument arrays, stderr
  captured into the error sentence.
- Every failure: one plain sentence + exit code from the shared register. Unknown verb
  option: error + usage, not ignored.

## Unit file

```ini
[Unit]
Description={description}
After=network.target

[Service]
Type=notify
ExecStart={execStart}
WorkingDirectory={appDir}
Restart=on-failure
RestartSec=5
{User=U}

[Install]
WantedBy=multi-user.target
```

Rendering is byte-deterministic (ordered keys, `\n`, invariant culture) so golden-file
tests run on any OS.

## Tests

| What | How | Platform-bound |
|---|---|---|
| VerbParser, ServiceIdentity | unit tests: verbs, options, `--`, precedence, validation | no |
| SystemdUnit | golden files (minimal / user / passthrough / dll vs apphost) | no |
| SystemdServiceManager | fake process runner: systemctl args + error mapping per verb | no |
| WindowsServiceManager | 031's `WindowsServiceVerbsTests` re-pointed; elevated round-trip recorded in RUNLOG when run | Windows |
| RunAsync | plain app runs/stops exit 0; Highway app round-trips against embedded Garnet; verb path never builds a host | no |
| Server host | existing verb behavior byte-identical after extraction | Windows |
