# Highway Samples

Three console apps that form a working distributed system: a broker, a service
host, and a storefront. Start them in three terminals and watch them talk over
TCP.

**Prerequisites: the .NET 10 SDK. Nothing else.** No Docker, no external broker,
no cloud account.

Every command below has been run. Output shown is real, captured from
`samples/RUNLOG.md` entry 2026-08-07.

---


## The three verbs

Choosing between them is one sentence: **one handler → Send, many handlers → Publish, need
the answer → Execute.**

| Storefront command | Verb | What happens |
|---|---|---|
| `order 2 WIDGET` | `ExecuteAsync` | RPC — one host answers, the caller waits |
| `invoice ORD-1` | `SendAsync` | Queue — **exactly one** processor handles it, nobody waits |
| `low WIDGET 2` | `PublishAsync` | Channel — **every** subscriber gets its own copy |

**Run two order services to see the difference.** `invoice` work is *shared* between them;
`low` is delivered to *both*. Same topology, different verb.

## Demonstrating dead letters

A poison message takes ~25 minutes to dead-letter with production defaults (5-minute lease
× 5 attempts). Shorten them for a demonstration:

```
dotnet run --project samples/Highway.Samples.Broker -- --lease-seconds 2 --max-attempts 2
```

Then `poison` in the storefront, wait a few seconds, and `dlq poison.queue`.

## Running with authentication

The default run is unauthenticated on loopback — the right configuration for trying
Highway, and the one this README shows first. To secure it:

```
dotnet run --project samples/Highway.Samples.Broker    -- --password sample-secret
dotnet run --project samples/Highway.Samples.OrderService -- --server "127.0.0.1:6500,password=sample-secret"
dotnet run --project samples/Highway.Samples.Storefront   -- --server "127.0.0.1:6500,password=sample-secret"
```

A client without the password is refused with a message naming the remedy. Note that
Highway redacts the password from its own logs and exceptions — the endpoint appears, the
credential does not.

**Binding beyond loopback requires a password.** `--bind 0.0.0.0` with no `--password`
fails at startup, deliberately: Highway can demand a password, and does so exactly where it
starts to matter.


## Run it

Three terminals, in this order. The broker must be up before the others.

```bash
# Terminal 1 — the broker
dotnet run --project samples/Highway.Samples.Broker

# Terminal 2 — hosts orders.create and orders.get, publishes OrderPlaced
dotnet run --project samples/Highway.Samples.OrderService

# Terminal 3 — calls services, subscribes to events
dotnet run --project samples/Highway.Samples.Storefront
```

The broker prints the line you need for the other two:

```
Highway broker
  listening on : 127.0.0.1:6500
  data dir     : C:\...\samples\Highway.Samples.Broker\data

Connect participants with:  --server 127.0.0.1:6500
```

At the storefront prompt:

```
> order 2 widget
  ORD-4106  2 x widget  total $19.98

  << event: OrderPlaced ORD-4106 — widget $19.98
```

That order crossed three OS processes: the storefront called the order service
through the broker, got a typed response back, and then received the event the
order service published.

---

## The projects

| Project | What it is |
|---|---|
| `Highway.Samples.Contracts` | Class library holding the shared DTOs. References **only** `Highway.Abstractions` — this is what the three-package split is for. |
| `Highway.Samples.Broker` | `Highway.Server` as a standalone process, via the public `HighwayServerBuilder` → `RunAsync` path. |
| `Highway.Samples.OrderService` | Hosts two services, publishes `OrderPlaced`, subscribes to `InventoryLow`. |
| `Highway.Samples.Storefront` | Calls services, subscribes to `OrderPlaced`, publishes `InventoryLow`. |

Both participants host *and* consume. Highway has no rigid client/server split.

## Storefront commands

```
order <qty> [item]     place an order      (RPC, typed response)
get <id>               fetch an order      (RPC, returns 404 as data)
cancel <id>            call a service nobody hosts (fast-fail 404)
low <item> [remaining] publish InventoryLow
discover [service]     which nodes host a service
stats [name]           server / service / channel counters
help                   this list
quit                   graceful shutdown
```

## Configuration

Argument, else environment variable, else default.

| Setting | Argument | Environment | Default |
|---|---|---|---|
| Broker port | `--port` | `HIGHWAY_PORT` | `6500` |
| Broker data dir | `--data-dir` | `HIGHWAY_DATA_DIR` | `./data` |
| Broker bind address | `--bind` | `HIGHWAY_BIND` | `127.0.0.1` |
| Participant broker | `--server` | `HIGHWAY_SERVER` | `127.0.0.1:6500` |
| Participant node name | `--node` | `HIGHWAY_NODE` | per app |

**Node names must be unique per running process.** A node's name is also its
subscriber-group identity, so two live processes sharing one will *compete* for
messages instead of each receiving a copy. That is why the scenarios below pass
`--node`.

---

## Scenarios

### 1. RPC across processes

```
> order 2 widget
  ORD-4106  2 x widget  total $19.98
```

The order service logs `Created ORD-4106: 2 x widget for customer 42`.

### 2. Errors are data, not exceptions

```
> get ORD-1
  status 404  ORDER_NOT_FOUND: No order with id 'ORD-1'.
```

`GetOrderService` returns a status code and an `ErrorDetail`. Nothing throws;
the caller reads the failure like any other field.

### 3. Calling a service nobody hosts

```
> cancel ORD-1
  status 404  SERVICE_NOT_FOUND: No live node currently hosts the service 'orders.cancel'.
  (failed in 1 ms — fast-fail via the registry, nothing enqueued)
```

`orders.cancel` is a real contract that nothing implements. The storefront sets
`FastFailEnabled = true`, so the client checks the registry and fails in
milliseconds. **With the default (`false`) this same call blocks for the full
30-second `CallTimeout`** — try it, it is worth feeling once.

### 4. A caller is also a publisher

```
> low bolt 2
  published InventoryLow: bolt (2 remaining)
```

The order service logs `Inventory low: bolt (2 remaining)`.

### 5. Fan-out — every node gets its own copy

Start a second storefront:

```bash
dotnet run --project samples/Highway.Samples.Storefront -- --node shop-2
```

Place an order from either one. **Both** print the event, because each node's
subscriber group is its node name.

### 6. Competing consumers

Start a second order service:

```bash
dotnet run --project samples/Highway.Samples.OrderService -- --node order-service-2
```

Place six orders. All six get responses, and the work splits between the two
hosts — verified 3/3 in the recorded run. Zero loss, zero duplicates.

### 7. Durable delivery across downtime

This is the claim worth checking yourself.

1. With `shop-1` running, place an order — it prints the event.
2. **Stop `shop-1`** (Ctrl+C).
3. Place two more orders from another storefront (`--node shop-9`).
4. **Restart `shop-1` with the same node name.** Give it a few seconds.

Both missed events arrive:

```
  << event: OrderPlaced ORD-4018 — beta $9.99
  << event: OrderPlaced ORD-5792 — gamma $9.99
```

The group is never unsubscribed, so messages queue for an absent node and drain
when it returns. Restart under a *different* node name and you get nothing —
that is a new group.

### 8. The broker restarting

Stop the broker and start it again on the same data directory. Events queued for
an offline subscriber survive, because AOF persisted them. Verified in the
recorded run.

### 9. Broker not running

```
Could not start: Could not connect to Highway server at '127.0.0.1:6499'.
Verify the server is running and the Server option is correct.
Is the broker running?  dotnet run --project samples/Highway.Samples.Broker
```

Names the endpoint, no stack trace.

### 10. Service host failover

Kill one order service while another runs. Calls keep succeeding through the
survivor.

### 11. Topology

```
> discover orders.create
  'orders.create' is hosted by:
    order-service-1  (last beat 4s ago)

> stats
    kind         server
    nodes        2
    services     2
    channels     2
```

> `discover` and `stats` drop to raw RESP via StackExchange.Redis, because
> `Highway.Client` exposes no public API for `HW.DISCOVER` / `HW.STATS`. See
> [RUNLOG.md](RUNLOG.md) finding 3.

---

## Across machines

The broker binds loopback by default. To reach it from another host:

```bash
dotnet run --project samples/Highway.Samples.Broker -- --bind 0.0.0.0
dotnet run --project samples/Highway.Samples.OrderService -- --server 192.168.1.50:6500
```

**Binding beyond loopback exposes the broker to your network.** Highway has no
authentication — anyone who can reach the port can call services, publish, and
read queued messages. Put it behind a firewall or a private subnet.

Verified over a real LAN interface in the recorded run: RPC and pub/sub both
work unchanged.

---

## Troubleshooting

**"Could not connect to Highway server"** — the broker is not running, or the
`--server` value does not match what the broker printed.

**"Address already in use"** — something else holds the port. Use
`--port 6600` on the broker and `--server 127.0.0.1:6600` on the participants.

**A restarted subscriber receives nothing** — either the node name changed (a
different name is a different group), or it quit before the consumer loop ran.
Give it a few seconds.

**Stale data between demos** — delete the broker's `data` directory. Queues,
groups and registrations all persist there by design.

**Build fails with "file is locked by Highway.Samples..."** — a sample is still
running. Stop it before rebuilding.

---

## Further reading

- [`docs/HIGHWAY-PROTOCOL.md`](../docs/HIGHWAY-PROTOCOL.md) — the wire protocol, in one file
- [`docs/product/product.md`](../docs/product/product.md) — what Highway is for
- [`RUNLOG.md`](RUNLOG.md) — every sample run, and what it found
