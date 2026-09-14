# Highway.Samples.Host

A minimal worker that demonstrates `HighwayHost.RunAsync` — the one-line host
from `Highway.Client.Hosting`. No Highway shapes, no broker. Just a console app
that can install itself as a Windows service or systemd unit.

## Run as a console app

```
dotnet run --project samples/Highway.Samples.Host
```

Output:
```
12:00:00 TickWorker started
12:00:00 tick — 12:00:00
12:00:05 tick — 12:00:05
^C
12:00:07 TickWorker stopped
```

Press Ctrl+C to stop.

## Install as a Windows service (elevated)

```
dotnet run --project samples/Highway.Samples.Host -- install --name highway-sample --start
dotnet run --project samples/Highway.Samples.Host -- status  --name highway-sample
dotnet run --project samples/Highway.Samples.Host -- stop    --name highway-sample
dotnet run --project samples/Highway.Samples.Host -- uninstall --name highway-sample
```

## Install as a systemd unit (root)

```
sudo dotnet run --project samples/Highway.Samples.Host -- install --name highway-sample --start
sudo dotnet run --project samples/Highway.Samples.Host -- status  --name highway-sample
sudo dotnet run --project samples/Highway.Samples.Host -- stop    --name highway-sample
sudo dotnet run --project samples/Highway.Samples.Host -- uninstall --name highway-sample
```

## With passthrough arguments

```
dotnet run --project samples/Highway.Samples.Host -- install --name highway-sample -- --some-arg value
```

Arguments after `--` are stored in the service's start command and passed to the
app on every service start.
