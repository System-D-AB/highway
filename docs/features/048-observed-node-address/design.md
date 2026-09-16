# Feature 048 — Design

## The map

`ObservedAddressRegistry` (Highway.Server.Resp) — a `ConcurrentDictionary<connectionId, (node,
endpoint)>`. `Record(connectionId, node, endpoint)` on `CLIENT SETNAME`; `Remove(connectionId)` on
teardown; `AddressOf(node)` scans values (node counts are small, dashboard reads rare) and returns
the observed endpoint or null. Keyed by connection so teardown is exact when one node holds several
connections.

## Wiring (all pieces already existed)

- `RespConnectionHandler` already has `connection.RemoteEndPoint` and passes it to `RespSession`, and
  already runs `session.OnConnectionClosed()` in its `finally`. So the endpoint and lifecycle hooks
  are free.
- `RespServer` owns the registry and exposes it on `IRespServerHost.ObservedAddresses`.
- `RespSession` gains optional `connectionId` + `ObservedAddressRegistry` params (defaults keep the
  session tests unchanged). `CLIENT` is no longer a blind `+OK`: `HandleClient` records on `SETNAME`
  (`Record(connectionId, name, remote.ToString())`) and `OnConnectionClosed` calls `Remove`.
- `StoreBrokerState` gains an optional registry param; `Nodes()` returns `dto with { SeenFrom =
  observed?.AddressOf(dto.Name) }`. `RespHighwayServer` (and `HighwayTestServer`) pass
  `server.ObservedAddresses`.

## Why the address is not the password-bearing anything

`CLIENT SETNAME` carries only the node name; the peer endpoint comes from the transport
(`RemoteEndPoint`), not from any client-supplied string. Nothing sensitive is stored or logged.

## Testing

| Layer | Proof |
|---|---|
| Registry | record → AddressOf; remove → null; empty inputs ignored |
| Session | `CLIENT SETNAME nodeA` records the remote address; `OnConnectionClosed` clears it |
| (verified by inspection) | the Highway client sets `ClientName = NodeName`, so SE.Redis sends `CLIENT SETNAME` on connect |

## Non-goals

No protocol change, no `CLIENT LIST` revival, no reachability probing.
