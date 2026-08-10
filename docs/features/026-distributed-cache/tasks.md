# Tasks: Distributed Cache

## Phase 0 — the implementation

- [x] T1 — `HighwayCache` core: `IDistributedCache`
  *Requirements:* R1.1–R1.6, R5.1–R5.4, R6.1–R6.3
  **Done when:** `HighwayCache` implements all `IDistributedCache` methods (Get, Set, Remove, Refresh — sync and async) using SE.Redis string commands against Garnet; keys are prefixed; absolute expiration and sliding expiration work independently; combined expiration stores the metadata header and computes correct TTL on Get; a `Get` past the absolute deadline returns null and deletes the key; tests for all round-trip, expiration, and isolation cases pass against `HighwayTestServer`.

- [x] T2 — `IBufferDistributedCache`
  *Requirements:* R2.1–R2.2
  *Depends on:* T1
  **Done when:** `HighwayCache` additionally implements `IBufferDistributedCache` with `TryGetAsync(key, IBufferWriter<byte>)` and `SetAsync(key, ReadOnlySequence<byte>, options)`; both paths round-trip correctly; a test verifies `HybridCache` uses the buffer path (no `byte[]` allocation on cache hit — asserted by registering both and checking the `IBufferDistributedCache` resolution).

- [x] T3 — Registration: `AddHighway` auto-registers cache
  *Requirements:* R3.1, R3.4, R8.1–R8.2
  *Depends on:* T1
  **Done when:** `ServiceCollectionExtensions.AddHighway` calls `TryAddSingleton<IDistributedCache, HighwayCache>()` and `TryAddSingleton<IBufferDistributedCache>(sp => sp.GetRequiredService<HighwayCache>())`; an existing `IDistributedCache` registration is not overridden (tested); the full existing test suite passes unchanged — no behavioral difference for applications that ignore caching.

- [x] T4 — Registration: standalone `AddHighwayCache`
  *Requirements:* R3.2–R3.4, R6.1
  *Depends on:* T1
  **Done when:** `AddHighwayCache(Action<HighwayCacheOptions>)` registers `HighwayCache` with its own `ConnectionMultiplexer` (no engine, no worker loops, no heartbeat); the server option is required (fail-fast if missing); a test proves the engine is NOT started; cache operations succeed against `HighwayTestServer`.

- [x] T5 — Connection sharing when both paths coexist
  *Requirements:* R3.4, R6.1
  *Depends on:* T3, T4
  **Done when:** when both `AddHighway` and `AddHighwayCache` are registered in the same container, a single `ConnectionMultiplexer` is used (asserted by object identity); the engine gives its multiplexer to `HighwayCache` at startup; a test proves one connection, not two.

## Phase 1 — `HybridCache` integration

- [x] T6 — Prove `HybridCache` works with Highway as L2
  *Requirements:* R4.1–R4.3
  *Depends on:* T3
  **Done when:** an integration test registers `AddHighway` + `AddHybridCache`, calls `GetOrCreateAsync<T>` — factory fires on first call, second call serves from L2 (factory not called), `RemoveAsync` invalidates, third call fires factory again; serialization uses `System.Text.Json`; the test runs against `HighwayTestServer`.

## Phase 2 — samples and documentation

- [x] T7 — Storefront sample: cache an order lookup
  *Requirements:* R7.1, R8.4
  *Depends on:* T3
  **Done when:** the storefront gains a `cache` command that demonstrates: first `get ORD-1` → calls the service (cache miss), second `get ORD-1` → served from cache (visible in output); a `cache-clear` command removes the entry; the RUNLOG captures the demonstration.

- [x] T8 — UserGuide: Distributed Cache section
  *Requirements:* R7.2–R7.3
  *Depends on:* T6
  **Done when:** the UserGuide gains a "Distributed Cache" section after Pub/Sub, following the same structure (concept → what you get → usage → behavior); states that Highway uses Garnet's native caching; shows `HybridCache` as the typed layer with a full registration example; mentions stampede protection, L1/L2 layering, and tag-based invalidation as `HybridCache` features that work automatically.

- [x] T9 — product.md implementation status update
  *Requirements:* R8.3
  *Depends on:* T6
  **Done when:** `docs/product/product.md`'s implementation status table gains a "Distributed Cache" row with status "Shipped — feature 026"; the Vision section mentions caching as an adjacent primitive (it already says "caching and locking chief among them" — annotate caching as now delivered).

- [x] T10 — Full verification
  *Requirements:* R8.1–R8.4
  *Depends on:* T7, T8, T9
  **Done when:** full test suite green; `dotnet build --no-incremental` zero warnings; samples run unchanged (caching additions are new commands, not changes to existing ones); RUNLOG updated.
