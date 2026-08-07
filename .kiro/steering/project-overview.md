# Project Overview

## What is Highway?

Highway is a distributed .NET framework that gives developers two verbs — `ExecuteAsync` (RPC) and `PublishAsync` (Pub/Sub) — and handles everything else: service discovery, load balancing, durable delivery, timeouts, and serialization.

- **Server**: A Garnet extension (`Highway.Server`) you run as a single binary
- **Client**: A NuGet package (`Highway.Client`) with assembly scanning, DI integration, and RESP-based communication
- **Abstractions**: Shared contracts package (`Highway.Abstractions`) — zero dependencies

## Target Framework

.NET 10 (LTS, ships November 2026). No netstandard2.0 support.

## Key Design Principles

1. Two verbs only: `ExecuteAsync` (RPC) and `PublishAsync` (Pub/Sub)
2. Four class shapes: Request, Response, Service, Channel message + Subscriber
3. Location transparency: same code runs in-process or distributed
4. Assembly scanning discovers everything at startup — no manual registration
5. At-least-once delivery for both RPC and Pub/Sub
6. Custom HW.* RESP commands — Highway.Server is the only supported broker
7. Atomicity lives server-side

## Package Architecture

```
Highway.Abstractions  → Contracts, interfaces, attributes, base classes (zero deps)
Highway.Client        → Engine, scanning, DI, serialization, sends HW.* commands
Highway.Server        → Garnet extension — the broker process with custom commands
```

## Product Documentation

Detailed product definition and research are in:
- `docs/product/product.md` — Full product definition, goals, protocol spec
- `docs/product/research.md` — Analysis of v0.8, Garnet research, migration plan
