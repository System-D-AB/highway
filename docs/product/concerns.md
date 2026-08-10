DONE_WITH_CONCERNS — Highway is usable for a medium-sized system with tens of services, queues, and channels, but its runtime is currently stronger than its developer governance. The principal risk is not reflection or route count; it is that topology, contract ownership, subscription identity, dependency direction, and resource multiplication remain too implicit.

No files were changed and no tests were run. This is a read-only architectural assessment of the current implementation.

Executive verdict
Highway’s core idea remains good:

Three understandable verbs.
Small, dependency-free contracts.
Explicit durable addresses that survive CLR renames.
Startup scanning rather than runtime reflection.
Frozen catalog lookups.
Scoped handlers.
Consistent queue, RPC, and pub/sub delivery machinery.
But for a medium-sized distributed system, Highway currently relies too heavily on developers remembering unwritten rules. That becomes architectural debt once several teams, libraries, and deployable nodes are involved.

My scores for the current state:

Area	Assessment
Core programming model	7/10
Annotation model	6/10
Runtime routing and delivery	7/10
Contract/version governance	4/10
Deployment/topology mental model	4/10
Medium-scale resource controls	5/10
Production availability	4/10
Overall medium-project suitability	6/10, usable with discipline
I would use Highway for a medium project only if the team adopts strong package, naming, dependency, and topology conventions. I would not yet present it as “reference the package, annotate classes, and everything remains simple” at that scale.

1. The actual programming model
The historical “two verbs and four shapes” description is no longer accurate. IHighwayClient exposes three verbs at IHighwayClient.cs:11-63:

ExecuteAsync — request/reply.
SendAsync — one logical consumer, competing instances.
PublishAsync — fan-out to subscriber groups.
The developer-facing roles are effectively:

Semantic role	Contract	Consumer
RPC	[Service] + IReturn<TResponse>	AsyncService<TRequest,TResponse>
Queue command/work	[Queue] + ISend	IProcess<T>
Published event	[Channel] + IPublish	ISubscribe<T>
RPC response	Output	Returned by RPC service
That is three verbs and seven roles/shapes, not two verbs and four shapes.

This is not inherently too complicated. “Need an answer / one consumer / many consumers” is a good rule. The debt is that older API comments and architectural material still describe RPC and pub/sub only—for example, IHighwayClient.cs:4—while the live product has queues as a first-class concept.

2. The mental model Highway should teach
The most important clarification is:

Class libraries do not host anything. A process hosts the union of the handlers Highway discovers from the assemblies loaded into that process.

A contract assembly declares communication. An implementation assembly contains handlers. The executable process is the deployment and failure boundary.

The model should be presented as:

                         CONTRACT
             "What can cross a process boundary?"
                              |
              +---------------+---------------+
              |               |               |
          RPC request     Queue command    Published event
          [Service]         [Queue]          [Channel]
              |               |               |
          ExecuteAsync      SendAsync       PublishAsync
              |               |               |
          one response      one logical     every logical
                            consumer         subscriber group
              |
              v
                    HOST / PROCESS / NODE
        "Which inbound capabilities run in this process?"

        RPC services   Queue processors   Event subscribers
There are two different questions:

What does this process provide?

RPC services it hosts.
Queues it processes.
Events it subscribes to.
What does this process use?

RPC services it calls.
Queues it sends to.
Events it publishes.
Publishers, senders, and RPC callers are not hosted infrastructure roles. They are ordinary outbound actions from application code. Services, processors, and subscribers are inbound hosted capabilities.

Highway currently makes the outbound side easy but leaves the inbound composition implicit.

3. The three-library problem
Suppose an executable references three implementation libraries:

Orders.Host
 ├── Orders.Application
 │    ├── CreateOrder RPC service
 │    ├── GenerateInvoice queue processor
 │    └── OrderCancelled subscriber
 │
 ├── Billing.Application
 │    ├── CapturePayment RPC service
 │    └── OrderCreated subscriber
 │
 └── Notifications.Application
      └── EmailCustomer queue processor
If all assemblies are scanned, the executable silently becomes a host for all of those inbound capabilities. At the same time, any code in those assemblies can call, send, or publish through IHighwayClient.

That is operationally valid, but difficult to reason about:

Adding an assembly reference may change what the executable hosts.
A shared library can unintentionally bring subscribers into several processes.
Deployment topology is not visible in the composition root.
Developers cannot easily answer “which process consumes this message?”
Architecture diagrams can drift from discovery behavior.
A class library can appear reusable while actually containing deployable infrastructure behavior.
Required convention
Only the executable composition root should decide what it hosts.

The target model should be closer to:

services.AddHighway(options =>
{
    options.AddContracts<OrdersContracts>();
    options.HostModule<OrdersModule>();
    options.HostModule<BillingModule>();
});
The exact API can differ, but the decision should be explicit. AppDomain scanning may remain as a convenience for samples and small applications, but medium systems need an explicit host manifest.

Highway should be able to print or generate:

Host: orders-api
Node: orders-api-pod-7c984

PROVIDES
  RPC    orders.create.v1          CreateOrderService
  RPC    orders.cancel.v1          CancelOrderService
  Queue  orders.fulfil.v1          FulfilOrderProcessor
  Group  orders                    inventory.reserved.v1

USES
  RPC    payments.authorize.v1
  Queue  notifications.email.v1
  Event  orders.created.v1
Without this, the “magic” saved in configuration is paid back as topology investigation.

4. Architectural findings
1. Logical subscriber identity is conflated with physical node identity
[HIGH] (confidence: 10/10) HighwayEngine.cs:174-176, SubscriptionWorkerLoop.cs:47 — NodeName is used both as the physical process identity and the pub/sub subscriber group.

That makes horizontally scaled subscriptions ambiguous:

Publish OrderCreated
     |
     +--> orders-api-pod-1   copy 1
     +--> orders-api-pod-2   copy 2
     +--> orders-api-pod-3   copy 3
Sometimes that is correct, such as local cache invalidation. Often it is wrong: three replicas of one logical Billing application should normally compete for Billing’s one event copy, while the Shipping application receives a separate copy.

The needed topology is:

OrderCreated
   |
   +--> group "billing"  --> billing-pod-1 / 2 / 3 compete
   |
   +--> group "shipping" --> shipping-pod-1 / 2 compete
Highway needs separate concepts:

NodeName: unique physical process identity.
SubscriptionGroup: stable logical consumer identity.
Possibly a per-subscriber group override.
Using the same NodeName across replicas is not a valid workaround because the option explicitly requires uniqueness and is also used as processing identity.

2. Route identity has no schema identity
[HIGH] (confidence: 9/10) DefaultTypeScanner.cs:125-147,174-210 — contracts map CLR types to string addresses, but contract-only declarations are not reverse-validated as one address to one wire schema.

Two packages can independently declare:

[Queue("billing.capture")]
public sealed record CapturePayment(...) : ISend;
with incompatible bodies. The broker sees only bytes and the same route string.

Hosted-service duplicates are checked, and channel collisions among local subscribers receive some validation, but there is no cross-node guarantee that every deployment agrees on:

route name -> message schema -> semantic owner -> version
A rolling deployment can therefore create wire-compatible-looking names with incompatible JSON bodies.

Recommended:

One owner per route.
Contract packages per bounded context.
Additive changes within a version.
Version route names for breaking changes.
Add a generated schema fingerprint to the node catalog.
Validate duplicate route names during build/startup.
3. Hidden synchronous RPC cycles can deadlock worker pools
[HIGH] (confidence: 9/10) HighwayEngine.cs:88-97, HighwayOptions.cs:89-93 — each RPC service has its own bounded worker pool, but there is no distributed call-graph or reentrancy protection.

Example:

8 requests enter A, filling A's 8 workers
        |
        v
each A calls B and waits
        |
        v
each B calls A and waits
        |
        v
A has no free workers
This is a distributed deadlock until timeouts unwind it. The compiler and DI container cannot detect it because the cycle exists in runtime message calls.

Highway should establish a hard convention:

Synchronous ExecuteAsync dependencies must form a directed acyclic graph.

Break cycles using:

Data returned earlier in the call chain.
Caller orchestration.
Queue commands.
Published events.
Explicit workflow/process state.
Tracing should also carry a Highway call path or hop count so an accidental A → B → A can fail clearly instead of appearing as generic timeouts.

4. Publishing placement exposes a dual-write problem
[HIGH] (confidence: 9/10) 
constraints.md
, C5 — Highway has no transactional enlistment or outbox.

A handler that changes a database and publishes an event has two independent writes:

Database commit succeeds
        |
        X process crashes
        |
PublishAsync never happens
Reversing the order creates the opposite inconsistency: the event is published but the database transaction later fails.

Therefore developers absolutely do need to think about where publishers live. Highway should not imply that calling PublishAsync from anywhere is harmless.

Convention:

Domain code raises in-memory domain events.
Application layer owns outbound communication.
Infrastructure adapter converts domain events into Highway contracts.
Critical state/event consistency requires an application outbox or an explicit reconciliation strategy.
Without an outbox, Highway is suitable where this failure window is acceptable or recoverable—not for workflows requiring atomic database-and-message publication.

5. Single broker availability limits production suitability
[HIGH] (confidence: 10/10) HighwayServerBuilder.cs:496 — clustering is disabled.

Highway provides durability but not broker high availability. For a medium system, this is a larger architectural constraint than annotation overhead:

Every RPC, send, and publish traverses one broker.
Broker maintenance interrupts all communication.
Disk corruption or host failure affects the entire system.
Durable restart is not failover.
This does not make Highway unusable, but the operational claim must be precise: it is a durable single-broker framework, not a highly available messaging platform.

6. Host topology is implicit in loaded assemblies
[MEDIUM] (confidence: 9/10) HighwayOptions.cs:202-211, DefaultTypeScanner.cs:11-30 — discovery depends on assemblies available to scanning.

This causes “reference equals deployment” behavior unless carefully controlled. The danger rises as shared implementation libraries proliferate.

Highway should generate a startup topology manifest and offer explicit module inclusion. A referenced contract package should never accidentally make a process a consumer.

7. Resource concurrency multiplies by route
[HIGH] (confidence: 10/10) HighwayEngine.cs:90-93,142-145 — WorkerConcurrency is applied independently to every RPC service and every queue.

With 30 RPC services and 20 queues at concurrency 8:

potential handler concurrency = (30 + 20) × 8 = 400
This can exhaust:

Database connection pools.
HTTP connection limits.
Memory.
Thread-pool continuations.
Downstream service quotas.
Highway needs both:

Per-route concurrency.
Node-global admission control.
Channel subscribers currently run with fixed concurrency 1, which helps ordering but can create a separate throughput bottleneck.

8. Storage limits are per queue, not global
[HIGH] (confidence: 10/10) HighwayServerOptions.cs:77, 
constraints.md
 C4.6/C4.7 — 1 GB is per queue/group, and AOF disk growth is measured as unbounded over historical writes.

With many channels and subscriber groups, the potential budget multiplies quickly. One inactive subscriber group can also block atomic fan-out once its queue fills.

A medium deployment needs:

Global broker storage budget.
Per-tenant or per-context budgets.
Disk-growth monitoring.
Group backlog alerts.
Documented remediation.
Ultimately, an AOF compaction/reclamation solution.
5. Annotation assessment
Annotations are not inherently the problem. Explicit route annotations are preferable to deriving durable addresses from CLR names.

For example, this is good:

[Queue("billing.invoice.generate.v1")]
public sealed record GenerateInvoice(...) : ISend;
Renaming GenerateInvoice does not strand messages in a newly inferred queue.

The debt comes from annotations being runtime declarations without compile-time governance.

Annotation strengths
Stable wire addresses.
Contracts remain dependency-light.
Discoverability from the DTO.
No central hand-maintained registration list.
Typed RPC response association through IReturn<T>.
Simple call-site API.
Annotation liabilities
1. Global strings lack compile-time ownership
[HIGH] (confidence: 9/10) ServiceAttribute.cs:7-13, ChannelAttribute.cs:6-12, QueueAttribute.cs:12-17 — names are global identifiers, but no analyzer enforces naming, ownership, uniqueness, or versioning.

2. AsyncService consumes the only base-class slot
[MEDIUM] (confidence: 10/10) AsyncService.cs:8-13 — the base class adds only one abstract method but prevents service implementations from inheriting an application-specific base class.

An interface such as IHandle<TRequest,TResponse> would compose better, although replacing the current API is unnecessary. Supporting an interface in addition to the base class would be sufficient.

3. Output couples domain responses to transport concerns
[MEDIUM] (confidence: 10/10) Output.cs:6-17 — every response inherits mutable HTTP-style transport fields.

Consequences:

Domain DTOs know transport status.
Immutable positional records become awkward.
HTTP semantics are projected into non-HTTP communication.
Success defaults depend on engine behavior rather than the type.
This is workable, but it is coupling debt.

4. RPC responses require a public parameterless constructor
[MEDIUM] (confidence: 10/10) DefaultTypeScanner.cs:188-190 — the engine requires construction of typed error responses.

This weakens immutability and makes record-centric contract design less natural.

5. Routing uses exact runtime type
[MEDIUM] (confidence: 10/10) HighwayClient.cs:67,179,225 — lookup is performed with request.GetType() or message.GetType().

A subclass, proxy, or generated mock is not routed as its registered base contract. Highway contracts should therefore be sealed concrete DTOs, and an analyzer should enforce that convention.

6. Multi-interface handlers are only partially discovered
[HIGH] (confidence: 10/10) DefaultTypeScanner.cs:81,229 — scanner logic uses FirstOrDefault for IProcess<T> and ISubscribe<T>.

A class implementing:

ISubscribe<OrderCreated>,
ISubscribe<OrderCancelled>
may be registered for only one interface. Which one is discovered should not be treated as a stable developer contract.

Immediate convention: one handler class per consumed contract.

Long-term fix: enumerate all closed interfaces or reject multiple interfaces with a precise startup error.

7. [Queue] permits structs that scanning cannot discover
[MEDIUM] (confidence: 10/10) QueueAttribute.cs:12, DefaultTypeScanner.cs:16-22 — the attribute permits classes and structs, but scanning filters everything to non-abstract classes.

That is an API contradiction.

8. [Idempotent] is stronger-sounding than its guarantee
[HIGH] (confidence: 10/10) IdempotentAttribute.cs:10-35 — it deduplicates redelivery of the same Highway message ID, not repeated business operations.

It does not deduplicate:

Two user clicks.
Caller retry creating a new request ID.
Two independently sent equivalent commands.
A replayed domain event with a new Highway ID.
An asynchronous feedback loop.
A name such as [DeduplicateRedelivery] would be more precise.

For pub/sub, the marker covers group delivery, not each individual local subscriber. If one subscriber succeeds and another fails, the successful subscriber may execute again after recovery. Business-level idempotency remains required.

Recommendation: keep attributes, add an analyzer and generated catalog
Do not replace annotations with a large fluent registration framework. That would trade annotation debt for configuration debt.

Add Roslyn analyzer/source-generator checks for:

Valid and normalized route names.
Duplicate route names.
Route-name/schema conflicts.
Missing attributes.
[Queue] target mismatch.
Multiple IProcess<T>/ISubscribe<T> interfaces.
Non-sealed contract DTOs.
Missing parameterless response constructor.
Dangerous singleton handlers.
Contract version convention.
Host capability manifest generation.
That preserves Highway’s simplicity while moving errors from runtime to build time.

6. Circular references and dependencies
Yes, circularity matters in distributed applications—more than in a single process—because many distributed cycles cannot be detected by the compiler.

There are four separate problems.

A. CLR object-graph cycles
Order -> Customer -> Orders -> Order
HighwayJson uses basic System.Text.Json options at HighwayEnvelope.cs:40-44, with no reference preservation. Cyclic object graphs normally fail serialization.

That is the correct default.

Distributed messages should be acyclic DTO snapshots:

public sealed record OrderCreated(
    Guid OrderId,
    Guid CustomerId,
    decimal Total);
They should not contain:

EF entities.
Lazy-loading proxies.
Bidirectional navigation properties.
Domain aggregate graphs.
Arbitrary polymorphic object trees.
Enabling $id/$ref globally would leak in-memory object identity into the wire contract and make compatibility harder.

B. DI constructor cycles
CreateOrderService
    -> IPaymentGateway
        -> CreateOrderService
Microsoft DI detects these when resolving the handler. However, handler activation occurs before the RPC exception-mapping try at ServiceExecutor.cs:78-81.

The likely result is retry/lease recovery and caller timeout rather than a clean typed 500 response. Activation and scope-creation failures should be inside the same deterministic error boundary as handler execution.

C. Synchronous distributed RPC cycles
Orders.ExecuteAsync
      |
      v
Payments.ExecuteAsync
      |
      v
Orders.ExecuteAsync
These are the most dangerous cycles because no CLR reference cycle needs to exist. The projects may have perfectly acyclic references while their runtime dependency graph is cyclic.

Required convention:

Synchronous RPC graph: DAG only

API/Orchestrator
   ├──> Orders
   ├──> Payments
   └──> Shipping

Orders ──> Payments ──> Fraud
                      X
                  no path back
D. Queue/pub-sub feedback cycles
OrderChanged event
    -> Pricing subscriber
        -> PriceChanged event
            -> Orders subscriber
                -> OrderChanged event
Every emission receives a new message ID, so [Idempotent] cannot stop this loop.

Intentional asynchronous cycles require:

Domain event ID.
Correlation/process ID.
Causation ID.
State-machine guard.
Terminal state.
Bounded transition count or hop count.
Idempotency keyed by domain identity and transition.
A self-feeding queue is acceptable only when every message proves monotonic progress—for example, chunk 4 of 20—not when it can regenerate equivalent work forever.

7. Recommended package and placement conventions
Contract ownership
Company.Orders.Contracts
 ├── Rpc/
 │    ├── CreateOrder.cs
 │    └── CreateOrderResult.cs
 ├── Commands/
 │    └── FulfilOrder.cs
 └── Events/
      └── OrderCreated.cs
Rules:

Contracts contain no handler implementations.
A route has one owning bounded context.
Consumers reference the owner’s contracts package.
No implementation project references another implementation project merely to communicate.
Breaking wire changes create a new version.
Host structure
Company.Orders.Application
 ├── Rpc/
 ├── Processors/
 ├── Subscribers/
 └── Outbound/

Company.Orders.Host
 ├── Program.cs
 └── HostProfile.cs
The executable explicitly selects modules to host.

Placement rules
RPC handler: in the application that owns the queried capability.
Queue processor: in the application that owns completion of that work.
Subscriber: in the application reacting to an external fact.
Publisher: application/infrastructure boundary after a domain transition—not inside entity classes.
RPC caller: application service or typed gateway, not domain entities.
IHighwayClient: preferably wrapped by intent-specific outbound interfaces rather than injected throughout every class.
For example:

public interface IPaymentAuthorizer
{
    Task<AuthorizationResult> AuthorizeAsync(
        OrderId orderId,
        Money total,
        CancellationToken ct);
}
The adapter uses IHighwayClient. Domain/application code depends on IPaymentAuthorizer, not on arbitrary route calls. This makes outbound dependencies visible and testable.

8. Naming convention
Use names that reveal owner, subject, action, and version:

RPC:
  orders.order.create.v1
  payments.authorization.request.v1

Queue:
  orders.fulfilment.start.v1
  notifications.email.send.v1

Event:
  orders.order.created.v1
  payments.authorization.declined.v1
Rules:

RPC names are capability/action-oriented.
Queue names are imperative work.
Channel names are past-tense facts.
Route names belong to the contract owner.
Breaking schema or meaning changes increment the route version.
CLR type renames do not change the route.
Moving a handler between hosts does not change the route.
9. Code-quality failure boundaries
Handler activation failures are not mapped consistently
[HIGH] (confidence: 10/10) ServiceExecutor.cs:78-81 — DI activation occurs before the handler execution try.

Constructor failures and DI cycles should become deterministic service errors rather than broker retries and caller timeouts.

Response serialization is outside service exception mapping
[HIGH] (confidence: 10/10) RpcWorkerLoop.cs:125 — a service can execute successfully and then fail while serializing its response.

A cyclic or unsupported response can therefore be redelivered, rerun, and eventually dead-lettered while the caller sees a timeout. This is especially dangerous for non-idempotent services.

The worker should map serialization failure to a deterministic error response where possible, then acknowledge the original request.

Remote stack traces are exposed
[HIGH] (confidence: 10/10) ServiceExecutor.cs:109-111 — ex.StackTrace is placed into remote ErrorDetail.

That can reveal:

Source paths.
Internal class names.
Infrastructure details.
Dependency versions.
Application structure.
Detailed traces should remain in structured server logs with a correlation ID. Remote callers should receive a safe error code and message.

Fixed JSON profile
[MEDIUM] (confidence: 9/10) HighwayEnvelope.cs:40-44 — serialization has one minimal fixed option set.

This avoids node-specific drift, which is good, but currently leaves no formal way to introduce shared converters, enum policy, or explicit polymorphic contracts.

Any future configuration must be a validated wire serialization profile, not arbitrary per-node JsonSerializerOptions, or deployments will disagree about the same route.

10. Scaling characteristics
What scales adequately
Scanning occurs at startup.
Catalog lookups use frozen dictionaries.
Heartbeats do not resend the catalog every time.
One route does not require hand-written registration.
Route counts in the tens are not intrinsically problematic.
Embedded integration testing lowers operational test cost.
What is not yet demonstrated
No characterized throughput claim exists.
No many-node soak evidence was found.
No hundreds-of-routes startup/resource benchmark was found.
NativeAOT/trimming safety is unproven.
Single-broker saturation behavior is not characterized.
Aggregate handler concurrency is not bounded.
Aggregate broker disk/memory is not bounded.
Tens of routes are therefore reasonable; claiming production behavior at hundreds of nodes or high sustained throughput would be premature.

11. Test assessment
Existing tests provide good evidence for:

Service and channel discovery.
Duplicate hosted-service rejection.
Caller-only contract discovery.
Scoped handler lifetime.
Typed RPC responses.
Mapped handler exceptions.
Queue competing consumers.
Independent pub/sub groups.
Capacity acquisition before claims.
Protocol command conformance.
Storage-key boundedness enumeration.
Coverage model:

Contract attributes
      |
      v
Type scanner -------------------- ★★ covered
      |                            GAP: multi-interface handlers
      |                            GAP: contract-only route collisions
      v
Immutable catalog --------------- ★★ covered
      |                            GAP: incompatible schemas on same route
      v
DI registration ----------------- ★★ covered
      |                            GAP: DI cycle/activation failure outcome
      v
Worker loops -------------------- ★★★ saturation/claim behavior
      |                            GAP: response serialization failure
      v
Broker commands ----------------- ★★★ protocol conformance
      |
      v
Distributed handler graph ------- GAP
                                   - A -> B -> A exhaustion
                                   - feedback loops
                                   - replica subscription semantics
                                   - many-node soak
High-priority missing tests:

Two contract-only request types using the same service name.
Two queue contract types using the same queue name.
One subscriber class implementing multiple ISubscribe<T> interfaces.
One processor implementing multiple IProcess<T> interfaces.
Struct queue contracts.
Cyclic request and response object graphs.
DI constructor cycle during service activation.
RPC A → B → A under saturated worker pools.
Event/queue feedback loop with new message IDs.
Multiple replicas of one logical subscriber application.
Many-route startup and aggregate-concurrency tests.
Trimmed/NativeAOT publish and execution.
Rolling deployments with incompatible body schemas.
12. Production failure modes
Failure	Current outcome	Clear to developer/operator?
Missing scanned assembly	Route locally appears unregistered	Partly; topology cause is hidden
Duplicate incompatible contract route	Payload reaches wrong schema	Often only runtime deserialization failure
DI cycle	Activation throws before RPC mapping	Likely timeout/retry rather than clear response
Cyclic response graph	Handler succeeds, serialization fails	Likely retry/dead-letter/timeout
RPC A → B → A	Worker starvation until timeout	Appears as latency/timeouts
Subscriber replica identity mistake	Every replica gets a copy	Silent semantic error
One dead subscriber group fills	Atomic publish refused for all groups	Visible, but broad blast radius
Broker host failure	Entire framework unavailable until restart	Visible; no failover
Many routes × concurrency	DB/HTTP pools exhausted	Indirect downstream failures
Database commit/publish crash gap	State and event disagree	Silent without reconciliation
The most dangerous failures are semantic rather than mechanical: they produce valid messages processed by the wrong number of consumers or omit events after successful state changes.

13. Minimum improvement strategy
Do not redesign the whole programming model. Split improvements into separate, controlled pieces.

Priority 1 — Mental model and build-time governance
Define contracts/handlers/hosts/uses vocabulary.
Add route naming and ownership conventions.
Add Roslyn analyzer and generated topology manifest.
Enforce one consumed contract per handler class.
Expose hosted and used capabilities at startup.
Priority 2 — Correct pub/sub topology
Separate physical NodeName from logical SubscriptionGroup.
Allow replicas to compete within a logical group.
Preserve per-node fan-out as an explicit option for cases such as cache invalidation.
Priority 3 — Deterministic failure handling
Put DI activation inside RPC error mapping.
Handle response serialization failures without rerunning successful business operations.
Stop returning stack traces remotely.
Add cycle/call-depth diagnostics.
Priority 4 — Medium-scale controls
Per-route concurrency.
Node-global concurrency.
Aggregate broker storage budget.
Route/schema fingerprints.
Many-node benchmarks and soak tests.
Each should be a separate feature. Combining analyzers, topology, subscription semantics, serialization, and resource controls would create too large a blast radius.

What already exists
Highway already has several foundations worth preserving:

The three-verb chooser: answer → Execute, one → Send, many → Publish.
Explicit durable route names.
Startup discovery and immutable catalogs.
Separate contract and implementation discovery.
Node catalog registration.
Structured activity/tracing support.
At-least-once semantics with dead-letter handling.
Detailed system constraints that openly document missing HA, global budgets, and bounded AOF growth.
The right solution is to make these foundations more explicit and enforceable—not replace them.

NOT in scope
This assessment does not propose:

Replacing Highway with MassTransit, NServiceBus, or gRPC.
Removing attributes.
Adding CLR reference preservation to JSON.
Making exactly-once delivery claims.
Building sagas or a universal workflow engine.
Modifying dashboard work or feature specifications.
Editing source or documentation.
Final answer
Highway is usable for a medium-sized project with tens of services, queues, channels, and nodes—but only if the system is treated as an explicit distributed topology, not merely a collection of annotated classes.

The attributes are not the primary liability. The larger debt is that they create a hidden global routing and hosting model without enough compile-time validation or deployment visibility.

The most important architectural changes are:

Separate logical subscription groups from physical nodes.
Generate and expose a host topology manifest.
Add analyzers for route ownership, collisions, versions, and handler shapes.
Require synchronous RPC dependencies to form a DAG.
Add correlation/causation/state guards for asynchronous cycles.
Establish strict contract/application/host package conventions.
Add global concurrency and storage controls.
Make DI and serialization failures deterministic.
Treat publication placement and the missing outbox as an explicit application concern.
State clearly that the current broker is durable but not highly available.