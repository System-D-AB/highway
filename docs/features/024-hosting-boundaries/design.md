# Design: Hosting Boundaries and Topology

## Overview

One principle drives every piece: **contracts and handlers already travel separate paths
through the scanner — this feature gives them separate assembly sets.** Today
`DefaultAssemblySource` produces one list and `DefaultTypeScanner.Scan` derives both handler
descriptors and contract maps from it. The change is a partition, not a rewrite:

```
                    DefaultAssemblySource
                            |
            +---------------+----------------+
            |                                |
   CONTRACT assemblies              HANDLER assemblies
   (full reference closure,         (chosen by HostingMode)
    unchanged, every mode)                   |
            |                     Implicit      = same as contract set
            |                     Declared      = entry ∪ declared modules
            |                     ExplicitOnly  = declared modules
            v                                v
   RequestContracts                  Services / Queues / Channels
   QueueContracts                    (the things this process HOSTS)
   MessageContracts
            \                                /
             +----------- ScanResult -------+
                            |
                    TopologyManifest  →  boot log, IHighwayEngine.Topology,
                                         registration catalog (can-use half)
```

Why the partition is safe: the scanner's discovery methods are already split —
`DiscoverServices`/`DiscoverChannels`/`DiscoverQueues` (handlers) versus
`DiscoverRequestContracts`/`DiscoverMessageContracts`/`DiscoverQueueContracts` (contracts).
No discovery method changes; only the type list each group receives.

## Interfaces and data model

```csharp
// Highway.Abstractions — the library-side declaration.
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class HighwayHostModuleAttribute : Attribute;

// Highway.Client
public enum HostingMode
{
    Implicit = 0,      // today's behavior; warnings make the accident visible (R2)
    Declared = 1,      // entry assembly + declared modules host
    ExplicitOnly = 2,  // declared modules only
}

public sealed class HighwayOptions
{
    public HostingMode HostingMode { get; set; } = HostingMode.Implicit;
    public List<Assembly> HostAssemblies { get; } = [];          // composition-root consent
    public HighwayOptions HostAssembly(Assembly assembly) { ... } // fluent convenience
}

// The manifest, exposed for hosts and tests (R3.4).
public sealed record TopologyManifest(
    string NodeName,
    IReadOnlyList<ProvidedCapability> Provides,   // kind, route, implementing type, assembly
    CanUse CanUse);                               // service/queue/channel contract names

public sealed record ProvidedCapability(
    CapabilityKind Kind,      // RpcService | QueueProcessor | Subscriber
    string Route,             // "orders.create", "invoices.generate", "orders.placed"
    string ImplementationType,
    string SourceAssembly);
```

## Key decisions

### D1 — `Implicit` stays the default; the flip is deferred, deliberately

Changing the default silently changes what deployed processes host — the exact class of
surprise this feature exists to end. Worse, in unit tests the *entry assembly is the test
host*, not the test project, so any entry-assembly-based default would unregister every
fixture handler in the suite. `Implicit` + warnings costs nothing and makes the accident
loud; teams opt into `Declared` when they feel the pain the warning names. The default flip
is a candidate for a major version, recorded in the deferred register — not smuggled in here.

### D2 — "Entry assembly" is `Assembly.GetEntryAssembly()`, with the test-host caveat named

In `Declared` mode under a unit-test runner, the entry assembly is `testhost`, so fixture
handlers must be declared (`[assembly: HighwayHostModule]` on the test project, or
`HostAssembly(...)` in the fixture). This is documented at the option, not discovered in a
failed test run. Tests for `Declared` mode itself declare their fixture assembly explicitly.

### D3 — Skipped handlers are reported, never silent (R1.5)

The scanner cannot log (it is pure); the engine can. `ScanResult` gains
`SkippedHandlerAssemblies` — computed by running handler discovery over the *excluded*
assemblies' types and recording what would have been hosted. The engine logs one line per
assembly. Cost: a second scan pass over excluded assemblies only, at startup, only in
non-Implicit modes. Correctness first: feature 013's precedent — refusing/reporting beats
silence — applies to *not* doing something exactly as it applies to doing it.

### D4 — The Implicit-mode warning is per assembly, not per handler (R2)

One warning line naming the assembly and its handlers. Per-handler warnings would make a
three-subscriber library four lines of noise and train operators to scroll past. The samples
log nothing (handlers in entry assembly); the test suite logs one line per fixture assembly
per engine — visible, not deafening.

### D5 — The can-use half rides the catalog JSON, additively (R4)

`CatalogInfo` gains `Uses` (three string lists). The registration catalog is JSON inside the
`NodeRegistration` value, so an old record without the field deserializes to empty — no
framing change, no version byte needed (the framing itself is untouched; this is the same
additive-JSON rule 022 used). `Catalogue.ReadNode` surfaces it; the node page renders it
under "Can use — reference-derived". The protocol document's registration-catalog schema
section is updated in this feature (protocol rule: same feature, same change).

### D6 — Manifest format: one block, grep-able, stable order

```
Highway topology — node order-service-1
  PROVIDES
    rpc    orders.create        CreateOrderService      (Orders.Application)
    queue  invoices.generate    InvoiceProcessor        (Orders.Application)
    sub    inventory.low        InventoryLowSubscriber  (Orders.Application)  group=order-service-1
  CAN USE (references the contract; not proof of calling)
    rpc    payments.authorize
    queue  notifications.email
    chan   orders.placed
```

Logged once at `StartAsync`, before worker loops start, ordered by kind then route so diffs
of boot logs are meaningful. The same data backs `IHighwayEngine.Topology`.

## Error handling

| Condition | Behavior |
|---|---|
| Declared module assembly contains no handlers | Warning: the declaration is dead — likely a wrong assembly |
| `ExplicitOnly` with zero declared modules and local handlers found | Warning naming the handlers that will not run; the process may legitimately be caller-only |
| Handler in undeclared assembly (`Declared`/`ExplicitOnly`) | Skipped + one log line per assembly (D3) |
| Handler in referenced assembly (`Implicit`) | Hosted + one warning per assembly (D4) |
| Same handler assembly declared twice (attribute + `HostAssembly`) | Idempotent; no error |

No new exceptions: every condition here is a policy outcome, not a fault. Errors remain data.

## What already exists (reuse, not rebuild)

- The scanner's contract/handler method split — the partition slots into it.
- `HighwayOptions.AdditionalAssemblies` — unchanged; adds to the *contract* closure as
  today, and in `Implicit` mode to handlers as today. `HostAssemblies` is the new,
  handler-specific consent list; the two are documented side by side.
- The catalog registration pipeline (006) — carries the can-use half with a field, not a
  mechanism.
- The dashboard node page (023 T6) — gains one section.

## Sequence: startup in `Declared` mode

```
AddHighway(o => { o.HostingMode = Declared; })
   |
   v
DefaultAssemblySource ──► full closure ──► contractAssemblies
   |                                          |
   |            entry ∪ [HighwayHostModule] ∪ o.HostAssemblies
   |                                          v
   |                                    handlerAssemblies
   v
scanner.Scan(contractAssemblies, handlerAssemblies)
   |        (excluded = closure − handlerAssemblies → SkippedHandlerAssemblies)
   v
catalog + TopologyManifest
   |
   ├─► engine.StartAsync: log manifest, log skipped-assembly lines
   └─► HW.REGISTER catalog JSON now carrying Uses
```

## Test strategy

Client unit tests (scanner partition, mode selection, skip reporting, manifest content) and
engine integration tests (manifest exposed, warning emitted in Implicit for a referenced-
assembly handler, Declared mode skips-and-reports). Server-side test for catalog `Uses`
round-trip through registration. **No dashboard tests, per standing instruction** — the node
page change is verified against the running samples and recorded in the RUNLOG.
