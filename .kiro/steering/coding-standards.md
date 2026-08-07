# Coding Standards

## Language & Framework

- C# 14 / .NET 10 (LTS)
- Nullable reference types enabled (`<Nullable>enable</Nullable>`)
- Implicit usings enabled
- File-scoped namespaces
- Target framework: `net10.0`

## Project Structure

```
src/
├── Highway.Abstractions/    # Contracts, interfaces, attributes, base classes
├── Highway.Client/          # Client library — engine, scanning, DI, serialization
└── Highway.Server/          # Garnet extension — broker with custom HW.* commands
tests/
├── Highway.Abstractions.Tests/
├── Highway.Client.Tests/
├── Highway.Server.Tests/
└── Highway.Integration.Tests/   # End-to-end tests with embedded Garnet
docs/
├── HIGHWAY-PROTOCOL.md      # THE wire protocol — single definition, test-enforced
├── product/                 # Vision, goals, research, roadmap (living documents)
└── features/                # Feature specs (requirements, design, tasks)
```

`samples/` joins this layout when feature 010 lands.

## Naming Conventions

- Namespaces: `Highway.Abstractions`, `Highway.Client`, `Highway.Server`
- Interfaces: `I` prefix (`IHighwayClient`, `IReturn<T>`, `IPublish`)
- Attributes: suffix with `Attribute` in class name, use short form in usage (`[Service("name")]`)
- Async methods: suffix with `Async`
- Test classes: suffix with `Tests` (e.g., `HighwayClientTests`)
- Test methods: `MethodName_Scenario_ExpectedResult`

## Code Style

- Use `var` when type is obvious from the right-hand side
- Prefer expression-bodied members for single-line implementations
- Use primary constructors where appropriate
- Records for immutable data transfer objects
- `readonly struct` for small value types in hot paths
- `sealed` on classes not designed for inheritance
- Always use `CancellationToken` in async APIs

## Dependencies

- Serialization: `System.Text.Json` (no Newtonsoft)
- Redis/Garnet client: `StackExchange.Redis` (SE.Redis)
- DI: `Microsoft.Extensions.DependencyInjection`
- Hosting: `Microsoft.Extensions.Hosting`
- Logging: `Microsoft.Extensions.Logging`
- Testing: xUnit + FluentAssertions + NSubstitute
- Server: `Microsoft.Garnet` (for Highway.Server only)

## Error Handling

- Services return errors via `Output.StatusCode` and `Output.ErrorDetail` — not exceptions
- Use HTTP-style status codes (200, 404, 500, 504, etc.)
- `ExecuteAsync` never throws to the caller — failures are data
- Internal exceptions are caught, logged, and mapped to status codes
- Use structured logging with `ILogger<T>`

## Performance Guidelines

- Cache reflection results (assembly scanning, type metadata)
- Use `ArrayPool<byte>` / `MemoryPool<byte>` for serialization buffers
- Prefer `ValueTask<T>` in hot paths where synchronous completion is common
- Use `Channel<T>` for internal async producer/consumer patterns
- Avoid allocations in the message dispatch loop

## Testing

- Unit tests for each package in isolation
- Integration tests use embedded Garnet (`HighwayTestServer`) — no external infrastructure
- Property-based testing for correctness properties (message ordering, delivery guarantees)
- All public APIs must have corresponding tests
- Test naming: `[Method]_[Scenario]_[ExpectedBehavior]`
