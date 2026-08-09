# Long-Running Work

Highway renews a handler's lease automatically, so a slow handler is safe without configuration.
This document is about the case renewal is *not* the right answer for.

## The short version

| Your handler takes | Do this |
|---|---|
| Seconds | Nothing. It already works |
| Minutes, under `MaxProcessingTime` (15 min) | Nothing. Renewal covers it |
| Hours | **Chunk it.** Read on |

## Why renewal is not the answer for hours

Renewal is bounded on purpose. `MaxProcessingTime` stops it at 15 minutes by default, because
unbounded renewal would delete lease recovery: a handler stuck in a deadlock would hold its
message forever — never redelivered, never dead-lettered, never visible as a problem.

You *can* raise the cap. But a two-hour handler has problems renewal does not solve:

- **A deploy kills it.** Shutdown cancels in-flight work after `DrainTimeout`, and two hours of
  progress is lost with it.
- **Progress is invisible.** Nobody can answer "how far along is it?" — not your dashboard, not
  your support team, not you.
- **It cannot be parallelised.** One message is one handler on one node, however many cores are
  idle next to it.
- **One bad row kills the whole job.** The message dead-letters after `MaxDeliveryAttempts` and
  you start again from nothing.

## Chunk and checkpoint

Each message does one slice, records where it got to **in your own database**, and enqueues the
next slice. Each message lives seconds; the job lives hours.

```
SendAsync(ReindexBatch { From = 0, Size = 1000 })
        │
        ▼
  ┌──────────────────────────────────────────┐
  │ guard: has this batch already been done? │ ── yes ─► acknowledge, stop
  └──────────────────┬───────────────────────┘
                     │ no
                     ▼
        process rows 0..999
                     │
                     ▼
        checkpoint progress in YOUR tables
                     │
                     ▼
        SendAsync(ReindexBatch { From = 1000, ... })  ── more work
                     │
                     ▼
        acknowledge  ── the message is done in seconds
```

```csharp
[Queue("reindex")]
public sealed record ReindexBatch : ISend
{
    public int From { get; init; }
    public int Size { get; init; }
    public required string JobId { get; init; }
}

public sealed class ReindexProcessor(AppDb db, IHighwayClient highway) : IProcess<ReindexBatch>
{
    public async Task ProcessAsync(ReindexBatch message, CancellationToken ct = default)
    {
        // GUARD FIRST. This one line gives you idempotency, out-of-order tolerance and
        // stale-redelivery safety together — see below.
        if (await db.IsBatchComplete(message.JobId, message.From, ct))
            return;

        var rows = await db.FetchRows(message.From, message.Size, ct);
        if (rows.Count == 0)
        {
            await db.MarkJobComplete(message.JobId, ct);
            return;
        }

        await Reindex(rows, ct);

        // Checkpoint BEFORE enqueuing the next slice. If the process dies between the two,
        // this batch is redelivered, the guard sees it is done, and the chain resumes.
        await db.MarkBatchComplete(message.JobId, message.From, ct);

        await highway.SendAsync(message with { From = message.From + message.Size });
    }
}
```

## The guard is the whole trick

`if (await db.IsBatchComplete(...)) return;` is one line and it buys three separate things:

1. **Idempotency.** At-least-once delivery means a message *can* arrive twice. The guard makes
   the second arrival free.
2. **Out-of-order tolerance.** Slices may be processed out of order after a redelivery. The
   guard means it does not matter.
3. **Stale-work safety.** If a handler ever *does* outlive its cap and gets duplicated, the
   second run stops at the guard rather than doing the work twice.

**Every handler should open with its guard**, not just chunked ones. It is the cheapest
correctness you will ever buy.

## `[Idempotent]` for handlers that cannot guard

When you genuinely cannot check "has this been done?" — an external API with no idempotency key
— use the attribute:

```csharp
[Queue("charge")]
[Idempotent(WindowSeconds = 900)]   // MUST exceed the worst-case handler duration
public sealed record ChargeCard : ISend { ... }
```

The window has to be longer than your slowest run. A window shorter than the handler expires
mid-flight and stops suppressing the duplicate it exists to suppress.

## Large payloads

`MaxPayloadBytes` is 1 MiB. Long-running work over large data passes a **reference**, not the
data:

```csharp
public sealed record ProcessUpload : ISend
{
    public required string BlobUri { get; init; }   // not the bytes
}
```

## Tuning, if you are staying with one long handler

| Setting | Default | Note |
|---|---|---|
| `MaxProcessingTime` | 15 min | The renewal cap. `TimeSpan.Zero` disables renewal entirely |
| `LeaseRenewalInterval` | 1 min | Keep the server's `Lease` at ≥ 3× this, or renewal is unreliable |
| `DrainTimeout` | 10 s | If `MaxProcessingTime` exceeds it, the engine warns at startup: a long handler **will** be cancelled mid-flight on shutdown and redelivered |

Raising `Lease` server-wide is the wrong lever. It is global, so a two-hour import also makes
RPC recovery take two hours — against a `CallTimeout` measured in seconds.

## Cross-references

- `docs/features/019-long-running-tasks/` — `HW.TOUCH` and the renewal design
- `docs/product/constraints.md` — C1.6 (a handler may run longer than the lease), C7.1
- `docs/HIGHWAY-PROTOCOL.md` — `HW.TOUCH`
