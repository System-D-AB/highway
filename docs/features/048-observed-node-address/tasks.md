# Feature 048 — Tasks

### - [x] T1 — The observed-address map and wiring

**Fulfills:** R1, R2
`ObservedAddressRegistry`; expose it on `IRespServerHost`/`RespServer`. `RespSession`: optional
`connectionId` + registry, `HandleClient` records on `CLIENT SETNAME`, `OnConnectionClosed` removes.
`StoreBrokerState.Nodes()` fills `SeenFrom` from the map; `RespHighwayServer`/`HighwayTestServer`
pass `server.ObservedAddresses`.
**Done when:** unit — registry record/remove/lookup; a `CLIENT SETNAME` records and a close clears
the observed address; existing session/host tests stay green.

### - [x] T2 — The record

**Fulfills:** R1.4
Spec captures the root cause, the observation-not-declaration framing (C7.3), and the sanitised-name
limitation. (The "Seen from" column and its "not connected" fallback already exist in the Nodes
view; no dashboard change is needed beyond the data now being populated.)
**Done when:** the spec explains why the column was dead and what now fills it.
