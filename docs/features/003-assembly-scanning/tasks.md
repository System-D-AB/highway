# Tasks: Assembly Scanning & Service Catalog

## Task Dependency Graph

```
T1 (CatalogInfo types in Abstractions)
T2 (ServiceDescriptor / ChannelDescriptor types) → depends on T1
T3 (IAssemblySource + DefaultAssemblySource) 
T4 (ITypeScanner + DefaultTypeScanner) → depends on T2, T3
T5 (IDelegateCompiler + ExpressionDelegateCompiler) → depends on T2
T6 (ICatalog + ImmutableCatalog) → depends on T2
T7 (DI registration logic) → depends on T4, T6
T8 (AddHighway extension method) → depends on T3, T4, T5, T6, T7
T9 (HighwayOptions updates) → depends on T3
T10 (Local dispatch in HighwayClient) → depends on T5, T6
T11 (Unit tests — scanning) → depends on T4
T12 (Unit tests — delegate compilation) → depends on T5
T13 (Unit tests — catalog) → depends on T6
T14 (Integration tests — AddHighway end-to-end) → depends on T8, T10
```

## Tasks

- [x] ### Task 1: Add CatalogInfo Types to Highway.Abstractions

**Fulfills:** Requirement 8

**Steps:**
1. Create `src/Highway.Abstractions/CatalogInfo.cs`
2. Create `src/Highway.Abstractions/CatalogServiceEntry.cs`
3. Create `src/Highway.Abstractions/CatalogChannelEntry.cs`
4. All types use `required` init properties, serializable with System.Text.Json

**Done criteria:**
- Types compile with zero dependencies
- Round-trip JSON serialization works
- Unit test validates serialization

---

- [x] ### Task 2: Create ServiceDescriptor and ChannelDescriptor Types

**Fulfills:** Requirement 4, 5

**Steps:**
1. Create `src/Highway.Client/Scanning/ServiceDescriptor.cs`
2. Create `src/Highway.Client/Scanning/ChannelDescriptor.cs`
3. Create `src/Highway.Client/Scanning/SubscriberDescriptor.cs`
4. Create `src/Highway.Client/Scanning/ScanResult.cs`
5. Include delegate properties (Func signatures for compiled dispatch)

**Done criteria:**
- All descriptor types defined with required properties
- Delegate signatures match ExecuteAsync/SubscribeAsync patterns

---

- [x] ### Task 3: Implement IAssemblySource

**Fulfills:** Requirement 1

**Steps:**
1. Create `src/Highway.Client/Scanning/IAssemblySource.cs` interface
2. Create `src/Highway.Client/Scanning/DefaultAssemblySource.cs`
3. Logic: scan `AppDomain.CurrentDomain.GetAssemblies()`, filter by Highway reference, exclude dynamic/reflection-only, add explicit assemblies, apply exclusions
4. Add `AdditionalAssemblies` and `ExcludedAssemblies` to `HighwayOptions`

**Done criteria:**
- Returns assemblies referencing Highway.Abstractions
- Excludes dynamic assemblies
- Respects AdditionalAssemblies and ExcludedAssemblies options
- Unit test with mock assemblies

---

- [x] ### Task 4: Implement ITypeScanner

**Fulfills:** Requirement 2, 3

**Steps:**
1. Create `src/Highway.Client/Scanning/ITypeScanner.cs` interface
2. Create `src/Highway.Client/Scanning/DefaultTypeScanner.cs`
3. Service discovery: find all non-abstract classes with base type `AsyncService<,>`, extract generic args, validate constraints, read `[Service]` attribute
4. Channel discovery: find all `ISubscribe<T>` implementations, group by message type, validate `[Channel]` attribute
5. Read `[ServiceLifetime]` attribute (default Scoped)
6. Throw typed exceptions on validation failures (use exceptions from Highway.Abstractions.Exceptions)

**Done criteria:**
- Discovers services correctly from test assemblies
- Discovers channels/subscribers correctly
- Throws correct exceptions for each validation failure
- Handles multiple subscribers per channel

---

- [x] ### Task 5: Implement IDelegateCompiler

**Fulfills:** Requirement 5

**Steps:**
1. Create `src/Highway.Client/Scanning/IDelegateCompiler.cs` interface
2. Create `src/Highway.Client/Scanning/ExpressionDelegateCompiler.cs`
3. `CompileServiceDelegate`: builds `Expression.Lambda` that casts object to service type, casts object to request type, calls `ExecuteAsync`, boxes result
4. `CompileSubscriberDelegate`: same pattern for `SubscribeAsync`
5. Handle async return types (`Task<TRes>` → `Task<object>`)

**Done criteria:**
- Compiled delegate invokes ExecuteAsync correctly
- Compiled delegate invokes SubscribeAsync correctly
- Performance: delegate invocation is < 100ns overhead vs direct call
- Unit test: compile delegate, invoke, verify result

---

- [x] ### Task 6: Implement ICatalog and ImmutableCatalog

**Fulfills:** Requirement 4

**Steps:**
1. Create `src/Highway.Client/Scanning/ICatalog.cs` interface
2. Create `src/Highway.Client/Scanning/ImmutableCatalog.cs`
3. Use `FrozenDictionary<string, ServiceDescriptor>` and `FrozenDictionary<string, ChannelDescriptor>`
4. Build a `Type → string` reverse lookup for request/message types (avoid per-call GetCustomAttribute)
5. Implement `ToCatalogInfo()` — excludes internal services/channels

**Done criteria:**
- O(1) lookup by service/channel name
- `ToCatalogInfo()` produces correct JSON-serializable DTO
- Internal services excluded from catalog info
- Thread-safe (immutable, no locks needed)

---

- [x] ### Task 7: DI Registration Logic

**Fulfills:** Requirement 6, 7

**Steps:**
1. Create `src/Highway.Client/Scanning/CatalogDiRegistrar.cs`
2. For each service: register implementation type with mapped lifetime (Scoped/Singleton/Transient)
3. For each subscriber: register implementation type with mapped lifetime
4. Map `HighwayServiceLifetime` → `Microsoft.Extensions.DependencyInjection.ServiceLifetime`
5. Register `ICatalog` as singleton
6. Register `IServiceScopeFactory` usage for per-request scopes

**Done criteria:**
- All discovered types registered in IServiceCollection
- Lifetime mapping is correct
- No duplicate registrations
- Scope creation works at runtime

---

- [x] ### Task 8: Implement AddHighway Extension Method

**Fulfills:** Requirement 7

**Steps:**
1. Create `src/Highway.Client/ServiceCollectionExtensions.cs`
2. `AddHighway()` — no-arg version for local-only mode
3. `AddHighway(Action<HighwayOptions>)` — with configuration
4. Orchestrate: AssemblySource → TypeScanner → DelegateCompiler → Catalog → DI Registration
5. Register `IHighwayClient` as singleton
6. Log discovered services/channels count via `ILogger`
7. Fail fast on scanning errors (don't catch validation exceptions)

**Done criteria:**
- `services.AddHighway()` completes without error for valid assemblies
- Throws immediately for invalid configurations
- `IHighwayClient` resolvable from DI after registration
- Works in local-only mode (no server configured)

---

- [x] ### Task 9: Update HighwayOptions

**Fulfills:** Requirement 1

**Steps:**
1. Add `List<Assembly> AdditionalAssemblies` property
2. Add `List<Func<Assembly, bool>> ExcludedAssemblies` property
3. Keep existing properties (NodeName, Server, CallTimeout)
4. Add XML doc comments

**Done criteria:**
- Options updated without breaking existing tests
- New properties have sensible defaults (empty lists)

---

- [x] ### Task 10: Implement Service Executor

**Fulfills:** Requirement 6 (scope per request)

**Steps:**
1. Create `src/Highway.Client/Execution/ServiceExecutor.cs`
2. Inject `ICatalog` and `IServiceScopeFactory`
3. `ExecuteServiceAsync(string serviceName, byte[] payload, CancellationToken ct)`: lookup service in catalog → create scope → resolve service → deserialize request → call compiled delegate → serialize response → dispose scope
4. `ExecuteSubscribersAsync(string channelName, byte[] payload, CancellationToken ct)`: lookup channel → for each subscriber → scope → resolve → call delegate → dispose → aggregate results
5. Handle errors: catch exceptions in service, set StatusCode = 500
6. Handle unknown service: return StatusCode = 404

**Done criteria:**
- Service executor resolves and invokes services correctly
- Scope created and disposed per invocation
- Unknown service returns 404
- Subscriber exceptions don't abort siblings
- Executor is usable by the worker loop (feature 005/006)

---

- [x] ### Task 11: Unit Tests — Assembly Scanning

**Fulfills:** Requirement 1, 2, 3

**Steps:**
1. Create test assemblies (or use test types in the test project)
2. Test: valid service discovered correctly
3. Test: valid channel with subscribers discovered
4. Test: missing [Service] attribute throws correct exception
5. Test: duplicate service name throws correct exception
6. Test: missing [Channel] attribute throws correct exception
7. Test: multiple subscribers on same channel all discovered
8. Test: excluded assemblies are skipped
9. Test: additional assemblies are included

**Done criteria:**
- All validation paths have test coverage
- Both happy-path and error-path scenarios tested

---

- [x] ### Task 12: Unit Tests — Delegate Compilation

**Fulfills:** Requirement 5

**Steps:**
1. Create test service and subscriber types
2. Test: compiled service delegate invokes ExecuteAsync and returns correct result
3. Test: compiled subscriber delegate invokes SubscribeAsync
4. Test: delegate handles CancellationToken correctly
5. Test: delegate handles async execution (awaits properly)

**Done criteria:**
- Delegates produce correct results
- Async/await works properly through compiled delegates

---

- [x] ### Task 13: Unit Tests — Catalog

**Fulfills:** Requirement 4

**Steps:**
1. Test: GetServiceDescriptor returns descriptor for registered service
2. Test: GetServiceDescriptor returns null for unknown service
3. Test: GetChannelDescriptor returns descriptor with all subscribers
4. Test: ToCatalogInfo excludes internal services
5. Test: AllServices/AllChannels returns complete lists

**Done criteria:**
- All ICatalog methods tested
- CatalogInfo serialization verified

---

- [x] ### Task 14: Integration Tests — AddHighway End-to-End

**Fulfills:** Requirement 7

**Steps:**
1. Create a test project with sample services and subscribers
2. Test: `AddHighway()` → resolve `IHighwayClient` → call `ExecuteAsync` → get correct response
3. Test: `AddHighway()` → resolve `IHighwayClient` → call `PublishAsync` → subscriber invoked
4. Test: scoped service gets fresh instance per call
5. Test: singleton service is same instance across calls
6. Test: service that throws → caller gets StatusCode 500
7. Test: call to unregistered service → StatusCode 404

**Done criteria:**
- Full end-to-end local dispatch works
- DI scoping behaves correctly
- Error handling produces correct status codes
