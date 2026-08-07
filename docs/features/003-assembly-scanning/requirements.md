# Feature: Assembly Scanning & Service Catalog

## Introduction

At startup, Highway.Client scans loaded assemblies to discover all services (`AsyncService<T,TRes>` subclasses) and channel subscribers (`ISubscribe<T>` implementations). It builds an immutable catalog of what the node offers, registers discovered types in the DI container, and caches all reflection metadata for zero-allocation dispatch at runtime. This is the "no manual wiring" promise — one attribute, one scan, done.

## Glossary

- **Assembly Scanning** — The process of inspecting loaded .NET assemblies to find Highway service and subscriber types
- **Catalog** — An immutable registry of discovered services and channels, built once at startup
- **Service Descriptor** — Metadata about a discovered service: name, implementation type, input type, output type, lifetime
- **Channel Descriptor** — Metadata about a discovered channel: name, message type, subscriber types with lifetimes
- **Compiled Delegate** — A pre-compiled expression tree that invokes a service's ExecuteAsync method without per-call reflection

## Requirements

### Requirement 1: Assembly Discovery

**User Story:** As a developer, I want Highway to automatically find all assemblies containing my services and subscribers, so that I don't have to manually register anything.

#### Acceptance Criteria

1. On startup, Highway scans all assemblies currently loaded in the AppDomain that reference `Highway.Abstractions`
2. Assemblies are filtered: dynamic assemblies and reflection-only assemblies are excluded
3. The Highway framework assemblies themselves (`Highway.Client`, `Highway.Abstractions`) are included in the scan (they may contain internal services)
4. An option to explicitly add additional assemblies is available via `HighwayOptions.AdditionalAssemblies` for cases where assemblies haven't been loaded yet
5. An option to exclude specific assemblies is available via `HighwayOptions.ExcludedAssemblies`
6. Assembly scanning completes and logs the count of discovered services/channels at startup

### Requirement 2: Service Discovery

**User Story:** As a developer, I want Highway to find all my `AsyncService<T,TRes>` implementations and register them by their `[Service]` attribute name, so that they are callable via `ExecuteAsync`.

#### Acceptance Criteria

1. All non-abstract classes inheriting from `AsyncService<T,TRes>` are discovered
2. For each discovered service, the input type `T` is validated: must implement `IReturn<TRes>`
3. For each discovered service, the output type `TRes` is validated: must derive from `Output`
4. The input type `T` is validated: must have a `[Service("name")]` attribute
5. Service names must be unique within the node — duplicate names throw `ServiceWithSameNameAlreadyExistsException`
6. Missing `[Service]` attribute throws `ServiceAttributeNotFoundException`
7. Invalid input type throws `ServiceInputTypeShouldImplementIReturnException`
8. Invalid output type throws `ServiceOutputTypeShouldImplementOutputException`
9. Each discovered service is registered in the DI container with the lifetime specified by `[ServiceLifetime]` attribute (default: Scoped)

### Requirement 3: Channel & Subscriber Discovery

**User Story:** As a developer, I want Highway to find all my `ISubscribe<T>` implementations and register them by their message type's `[Channel]` attribute, so that they receive published messages.

#### Acceptance Criteria

1. All non-abstract classes implementing `ISubscribe<T>` are discovered
2. The message type `T` is validated: must implement `IPublish`
3. The message type `T` is validated: must have a `[Channel("name")]` attribute
4. Multiple subscribers can bind to the same channel (fan-out)
5. Missing `[Channel]` attribute throws `ChannelAttributeMissingException`
6. Duplicate channel names (same name on different message types) throw `ChannelAlreadyAddedException`
7. Each discovered subscriber is registered in the DI container with the lifetime specified by `[ServiceLifetime]` attribute (default: Scoped)

### Requirement 4: Immutable Service Catalog

**User Story:** As the Highway engine, I want an immutable catalog of all discovered services and channels available after startup, so that I can dispatch requests efficiently without locks or mutations.

#### Acceptance Criteria

1. The catalog is built once during startup and is immutable thereafter
2. `ICatalog.GetServiceDescriptor(string serviceName)` returns the service metadata or null
3. `ICatalog.GetChannelDescriptor(string channelName)` returns the channel metadata or null
4. `ICatalog.IsServiceRegistered(string serviceName)` returns true/false
5. `ICatalog.IsChannelRegistered(string channelName)` returns true/false
6. `ICatalog.AllServices` returns all registered service descriptors (for heartbeat catalog generation)
7. `ICatalog.AllChannels` returns all registered channel descriptors
8. Catalog lookups are O(1) dictionary access

### Requirement 5: Compiled Dispatch Delegates

**User Story:** As the Highway engine, I want pre-compiled delegates for invoking `ExecuteAsync` and `SubscribeAsync` on discovered types, so that there is zero reflection overhead at runtime.

#### Acceptance Criteria

1. For each discovered service, a compiled delegate for `ExecuteAsync(TRequest, CancellationToken)` is created at startup
2. For each discovered subscriber, a compiled delegate for `SubscribeAsync(T, CancellationToken)` is created at startup
3. Delegates are stored in the catalog alongside the service/channel descriptors
4. Runtime dispatch uses the cached delegate — no `MethodInfo.Invoke`, no `ObjectMethodExecutor.Create` per call
5. Delegate compilation happens during startup (acceptable to spend time here)

### Requirement 6: DI Scope Per Request

**User Story:** As a developer, I want each service invocation to run in its own DI scope, so that scoped dependencies (like DbContext) are properly isolated per request.

#### Acceptance Criteria

1. Each `ExecuteAsync` invocation creates a new `IServiceScope`
2. The service instance is resolved from the scope's `IServiceProvider`
3. The scope is disposed after `ExecuteAsync` completes (success or failure)
4. Each `SubscribeAsync` invocation creates a new `IServiceScope`
5. Subscriber instances are resolved from the scope
6. Singleton services are resolved from the root provider (standard DI behavior)
7. Transient services get a new instance per resolution within the scope

### Requirement 7: AddHighway Extension Method

**User Story:** As a developer, I want a single `services.AddHighway()` call to perform all scanning, catalog building, and DI registration, so that wiring my application is one line.

#### Acceptance Criteria

1. `services.AddHighway(Action<HighwayOptions>)` performs assembly scanning, builds the catalog, registers all discovered types, and registers Highway infrastructure services
2. `HighwayOptions.Server` is required — calling `AddHighway` without configuring a server throws `InvalidOperationException` at startup
3. `IHighwayClient` is registered as a singleton in the DI container
4. The catalog (`ICatalog`) is registered as a singleton
5. Startup scanning errors are thrown immediately — not swallowed — so the app fails fast on misconfiguration
6. For testing, `HighwayTestServer` provides an embedded server connection string

### Requirement 8: Catalog Serialization for Heartbeat

**User Story:** As the Highway client in distributed mode, I want to serialize the local catalog to JSON for the `HW.HEARTBEAT` command, so that the server knows what this node offers.

#### Acceptance Criteria

1. `ICatalog` can produce a `CatalogInfo` DTO containing: list of service names with input/output type names, list of channel names with subscriber count
2. `CatalogInfo` is serializable to JSON via System.Text.Json
3. Internal/private services (highway.internal.*, highway.private.*) are excluded from the heartbeat catalog
4. The serialized catalog is compact (just names and metadata, no types or delegates)
