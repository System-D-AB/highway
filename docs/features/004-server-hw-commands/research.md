# Research: Garnet Extensibility for Highway HW.* Commands

**Purpose:** Verify, against the exact Garnet source Highway builds against, how Highway.Server can register custom `HW.*` RESP commands, what those commands can and cannot do, and how the doorbell (notification) mechanism must work. This document supersedes the Garnet-extensibility assumptions in `docs/product/research.md` (§2.3–2.4), which were written against an older Garnet version.

**Verified against:** `libs/garnet` submodule at commit `8b329e30` — tag `v2.1.2` + 2 commits (`ca8faef5`, `8b329e30`), the latest release as of 2026-08-06. All file:line references below point into that checkout.

**Method:** Direct source inspection of the Garnet submodule (registration paths, custom command base classes, pub/sub broker internals, AOF replay, hosting options) plus in-repo samples and test fixtures. Nothing here is inferred from external documentation.

> **Global note:** This Garnet version has been heavily refactored relative to older docs. `ArgSlice` is now `PinnedSpanByte`, `MemoryResult<byte>` output writers are now `RespMemoryWriter` for store functions, `RegisterCmd`/`CustomCommandRegistry` no longer exist, and everything routes through `CustomCommandManager` + `RegisterApi`/`ModuleBase`.

---

## 1. Programmatic Registration

**ABSENT:** There is no `CustomCommandRegistry` class and no `RegisterCmd`/`RegisterCustomCommand` methods anywhere in `libs/` in this checkout (grep verified). The old registration API was replaced by `RegisterApi` + a module system.

### GarnetServer construction — `libs/host/GarnetServer.cs`

Two public constructors:

```csharp
// Line 84 — config-file/command-line based (NOT what Highway wants)
public GarnetServer(string[] commandLineArgs, ILoggerFactory loggerFactory = null,
    bool cleanupDir = false, IAuthenticationSettings authenticationSettingsOverride = null)

// Line 160 — pure programmatic, the one for embedded hosting
public GarnetServer(GarnetServerOptions opts, ILoggerFactory loggerFactory = null,
    IGarnetServer[] servers = null, bool cleanupDir = false)
```

Public members relevant to extensibility:

```csharp
public RegisterApi Register;        // "Command registration API"
public MetricsApi Metrics;
public StoreApi Store;
protected StoreWrapper storeWrapper; // line 57 — protected, key for §3
public void Start();                 // runs RecoverAsync then starts listeners
```

### RegisterApi — `libs/server/Servers/RegisterApi.cs`

Exact signatures (all delegate to `provider.StoreWrapper.customCommandManager`):

```csharp
public int NewCommand(string name, CommandType type, CustomRawStringFunctions customFunctions,
    RespCommandsInfo commandInfo = null, RespCommandDocs commandDocs = null, long expirationTicks = 0)

public int NewTransactionProc(string name, Func<CustomTransactionProcedure> proc,
    RespCommandsInfo commandInfo = null, RespCommandDocs commandDocs = null)

public int NewType(CustomObjectFactory factory)

public (int objectTypeId, int subCommandId) NewCommand(string name, CommandType commandType,
    CustomObjectFactory factory, CustomObjectFunctions customObjectFunctions,
    RespCommandsInfo commandInfo = null, RespCommandDocs commandDocs = null)

public int NewProcedure(string name, Func<CustomProcedure> customProcedure,
    RespCommandsInfo commandInfo = null, RespCommandDocs commandDocs = null)

public bool NewModule(ModuleBase module, string[] moduleArgs,
    out ReadOnlySpan<byte> errorMessage, ILogger logger = null)
```

`expirationTicks` semantics (from the XML doc): `-1` removes existing expiry metadata, `0` = keep current/default (no expiry), `>0` sets expiry.

### Module path (alternative) — `libs/server/Module/ModuleRegistrar.cs`

```csharp
public abstract class ModuleBase
{
    public abstract void OnLoad(ModuleLoadContext context, string[] args); // must have parameterless ctor
}

public class ModuleLoadContext
{
    public readonly ILogger Logger;
    public ModuleActionStatus Initialize(string name, uint version);
    public ModuleActionStatus RegisterCommand(string name, CustomRawStringFunctions customFunctions,
        CommandType type = CommandType.ReadModifyWrite, RespCommandsInfo commandInfo = null,
        RespCommandDocs commandDocs = null, long expirationTicks = 0);
    public ModuleActionStatus RegisterTransaction(string name, Func<CustomTransactionProcedure> proc,
        RespCommandsInfo commandInfo = null, RespCommandDocs commandDocs = null);
    public ModuleActionStatus RegisterType(CustomObjectFactory factory);
    public ModuleActionStatus RegisterCommand(string name, CustomObjectFactory factory,
        CustomObjectFunctions command, CommandType type = CommandType.ReadModifyWrite,
        RespCommandsInfo commandInfo = null, RespCommandDocs commandDocs = null);
    public ModuleActionStatus RegisterProcedure(string name, Func<CustomProcedure> customScriptProc,
        RespCommandsInfo commandInfo = null, RespCommandDocs commandDocs = null);
}

public sealed class ModuleRegistrar // singleton
{
    public static ModuleRegistrar Instance { get; }
    public bool LoadModule(CustomCommandManager customCommandManager, Assembly loadedAssembly,
        string[] moduleArgs, ILogger logger, out ReadOnlySpan<byte> errorMessage);
}
```

The underlying registry is `public class CustomCommandManager` (`libs/server/Custom/CustomCommandManager.cs`), but all its `Register(...)` overloads are `internal`; you must go through `RegisterApi` or `ModuleLoadContext`. `GarnetServerOptions.LoadModuleCS` loads module assemblies at startup (`LoadModules` in GarnetServer.cs), and `RegisterModule` is `public` on the manager if you ever obtain it.

**Registration timing (verified from tests):** registration works AFTER `server.Start()` (`RespCustomCommandTests.cs:269-289` starts the server then registers), BUT for AOF recovery, tests always register BEFORE `Start()` (`RespAofTests.cs:722-725`, `749-752`: `CreateGarnetServer(...tryRecover: true, enableAOF: true)` → `RegisterCustomCommand(server)` → `server.Start()`), because `Start()` runs `Provider.RecoverAsync()` first. **Highway must register before Start.**

---

## 2. Custom Command Shapes

All in `libs/server/Custom/`.

**ABSENT:** `CustomRawCommandBase` does not exist, and `MainAsync` appears nowhere in `libs/server`. The "arbitrary async command" shape from older Garnet docs is gone; raw commands are strictly RMW/Read store functions. `ICustomCommand` (`ICustomCommand.cs`) is an *internal* interface holding only `byte[] Name { get; }` — not a user-facing extension point.

### a) Raw-string command: `CustomRawStringFunctions` — `CustomRawStringFunctions.cs`

```csharp
public abstract class CustomRawStringFunctions
{
    protected static unsafe ReadOnlySpan<byte> GetNextArg(ref StringInput input, scoped ref int offset);
    protected static ReadOnlySpan<byte> GetFirstArg(ref StringInput input);

    public virtual bool NeedInitialUpdate(scoped ReadOnlySpan<byte> key, ref StringInput input, ref RespMemoryWriter writer) => true;
    public virtual bool NeedCopyUpdate(ReadOnlySpan<byte> key, ref StringInput input, ReadOnlySpan<byte> oldValue, ref RespMemoryWriter writer) => true;
    public abstract int GetInitialLength(ref StringInput input);
    public abstract int GetLength(ReadOnlySpan<byte> value, ref StringInput input);
    public abstract bool InitialUpdater(ReadOnlySpan<byte> key, ref StringInput input, Span<byte> value, ref RespMemoryWriter writer, ref RMWInfo rmwInfo);
    public abstract bool InPlaceUpdater(ReadOnlySpan<byte> key, ref StringInput input, Span<byte> value, ref int valueLength, ref RespMemoryWriter writer, ref RMWInfo rmwInfo);
    public abstract bool CopyUpdater(ReadOnlySpan<byte> key, ref StringInput input, ReadOnlySpan<byte> oldValue, Span<byte> newValue, ref RespMemoryWriter writer, ref RMWInfo rmwInfo);
    public abstract bool Reader(ReadOnlySpan<byte> key, ref StringInput input, ReadOnlySpan<byte> value, ref RespMemoryWriter writer, ref ReadInfo readInfo);
    public virtual void NotFound(ReadOnlySpan<byte> key, ref StringInput input, ref RespMemoryWriter writer) => writer.WriteNull();
}
```

`StringInput` (`libs/server/InputHeader.cs:309`): `public struct StringInput : IStoreInput` with fields `RespInputHeader header; long arg1; SessionParseState parseState;` — arguments live in `parseState`, accessed via the protected `GetNextArg`/`GetFirstArg`. `arg1` carries the expiry ticks when `expirationTicks > 0` (see `CustomRespCommands.cs` `TryCustomRawStringCommand`).

### b) Transaction: `CustomTransactionProcedure` — `CustomTransactionProcedure.cs`

```csharp
public abstract class CustomTransactionProcedure : CustomProcedureBase
{
    public virtual bool FailFastOnKeyLockFailure => false;
    public virtual TimeSpan KeyLockTimeout => TimeSpan.FromMilliseconds(100); // 5000 in DEBUG

    protected void AddKey(PinnedSpanByte key, LockType type, StoreType storeType);
    protected bool RewindScratchBuffer(PinnedSpanByte slice);
    protected PinnedSpanByte CreateArgSlice(ReadOnlySpan<byte> bytes);
    protected PinnedSpanByte CreateArgSlice(string str);

    public abstract bool Prepare<TGarnetReadApi>(TGarnetReadApi api, ref CustomProcedureInput procInput)
        where TGarnetReadApi : IGarnetReadApi;
    public abstract void Main<TGarnetApi>(TGarnetApi api, ref CustomProcedureInput procInput, ref MemoryResult<byte> output)
        where TGarnetApi : IGarnetApi;
    public virtual void Finalize<TGarnetApi>(TGarnetApi api, ref CustomProcedureInput procInput, ref MemoryResult<byte> output)
        where TGarnetApi : IGarnetApi { }
}
```

Doc note on `Finalize` (verbatim from the file): *"Finalize is considered post transaction processing and therefore is not executed at recovery time."*

### c) Shared base: `CustomProcedureBase` — `CustomProcedureBase.cs`

Argument parsing + RESP output helpers (all `protected static`):

```csharp
protected static unsafe PinnedSpanByte GetNextArg(ref SessionParseState parseState, ref int idx);
protected static unsafe PinnedSpanByte GetNextArg(ref CustomProcedureInput procInput, ref int idx);
protected static unsafe void WriteSimpleString(ref MemoryResult<byte> output, ReadOnlySpan<char> simpleString);
protected static unsafe void WriteBulkStringArray(ref MemoryResult<byte> output, params PinnedSpanByte[] values);
protected static unsafe void WriteBulkStringArray(ref MemoryResult<byte> output, List<PinnedSpanByte> values);
protected static unsafe void WriteBulkString(ref MemoryResult<byte> output, Span<byte> simpleString);
protected static unsafe void WriteNullBulkString(ref (IMemoryOwner<byte>, int) output);
protected static unsafe void WriteError(ref MemoryResult<byte> output, ReadOnlySpan<char> errorMessage);
// plus: ParseCustomRawStringCommand / ParseCustomObjectCommand / ExecuteCustomRawStringCommand / ExecuteCustomObjectCommand
// (these use the internal respServerSession field — see §3)
```

### d) Procedure (non-transactional): `CustomProcedure` — `CustomProcedureWrapper.cs`

```csharp
public abstract class CustomProcedure : CustomProcedureBase
{
    public abstract bool Execute<TGarnetApi>(TGarnetApi garnetApi, ref CustomProcedureInput procInput, ref MemoryResult<byte> output)
        where TGarnetApi : IGarnetApi;
}
```

### e) Custom object type: `CustomObjectFactory` / `CustomObjectBase` / `CustomObjectFunctions`

`CustomObjectFactory.cs`:

```csharp
public abstract class CustomObjectFactory
{
    public abstract CustomObjectBase Create(byte type);
    public abstract CustomObjectBase Deserialize(byte type, BinaryReader reader);
}
```

`CustomObjectFunctions.cs` (virtual methods, all throw `NotImplementedException` by default except noted):

```csharp
public virtual bool NeedInitialUpdate(scoped ReadOnlySpan<byte> key, ref ObjectInput input, ref RespMemoryWriter writer);
public virtual bool InitialUpdater(ReadOnlySpan<byte> key, ref ObjectInput input, IGarnetObject value, ref RespMemoryWriter writer, ref RMWInfo rmwInfo); // defaults to Updater
public virtual bool Updater(ReadOnlySpan<byte> key, ref ObjectInput input, IGarnetObject value, ref RespMemoryWriter writer, ref RMWInfo rmwInfo);
public virtual bool Reader(ReadOnlySpan<byte> key, ref ObjectInput input, IGarnetObject value, ref RespMemoryWriter writer, ref ReadInfo readInfo);
// plus NotFound(...)
```

`CustomObjectBase` (`CustomObjectBase.cs`) requires `SerializeObject(BinaryWriter)`, `CloneObject()`, `Dispose()`; it is serialized via `GarnetObjectSerializer` for checkpoint/AOF purposes.

### Key types

- `PinnedSpanByte` (replaces `ArgSlice`): `libs/storage/Tsavorite/cs/src/core/VarLen/PinnedSpanByte.cs` — `public unsafe struct PinnedSpanByte { public byte* ptr; public int length; }` with `ReadOnlySpan`, `Span`, `FromPinnedPointer(byte*, int)`, `FromPinnedSpan(ReadOnlySpan<byte>)` (memory MUST be pinned).
- `MemoryResult<T>`: `libs/common/MemoryResult.cs` (`public struct MemoryResult<T> : IDisposable` with `IMemoryOwner<T> MemoryOwner; int Length;`).
- `CustomProcedureInput`: `InputHeader.cs:556`, `public struct CustomProcedureInput : IStoreInput` (wraps `SessionParseState parseState`).

---

## 3. PUBLISH from Custom Code — CRITICAL

### 3.1 The custom-command API surface CANNOT publish — still true in this checkout

- **`IGarnetApi` / `IGarnetReadApi` / `IGarnetAdvancedApi` have zero pub/sub surface.** `grep -i publish` over `libs/server/API/` returns **no matches**. The interfaces (`IGarnetApi.cs`, 2218 lines) contain only KV/list/set/hash/zset/geo/expire operations. The prior-research conclusion holds.
- **No publish surface in the host library either:** `grep -i publish` over `libs/host/` returns **no matches**.
- **`RespServerSession.subscribeBroker` is private**: `libs/server/Resp/PubSubCommands.cs:16` — `readonly SubscribeBroker subscribeBroker;` inside `internal sealed unsafe partial class RespServerSession`. (`RespServerSession` itself is `internal`.)
- **Custom procedures hold a session reference but it is `internal`**: `CustomProcedureBase.cs` — `internal RespServerSession respServerSession;`, and `CustomTransactionProcedure` has `internal TransactionManager txnManager;`. `InternalsVisibleTo` (`libs/server/Properties/AssemblyInfo.cs`) lists only Garnet's own test/benchmark assemblies (`Garnet.test*`, `Garnet.fuzz`, `Embedded.perftest`, `BDN.benchmarks`, `Resp.benchmark`) — **not** Highway.
- **`ModuleLoadContext` exposes only `Logger` + registration methods** — no server/session/broker reference.
- **No attributes/events/notification hooks**: no `public event` on `RespServerSession`; nothing resembling server-push callbacks.

### 3.2 Release notes / git history check

- **There is no RELEASENOTES.md / CHANGELOG file** in the checkout (glob for `*RELEASENOTES*/*CHANGELOG*/*HISTORY*` finds nothing; the website has only blog posts).
- Git history search (`git log --grep="publish" -i`, `--grep="custom" -i`) contains **no commit adding publish capability to custom commands, transactions, or objects**. Closest hits:
  - `5dbf7177` "Fix `PUBLISH` failing when called from Lua scripts (#1927)" — confirms the only non-RESP publish path is Lua.
  - `cdafd4c2` "Custom Procedure Replication Propagation (#1252)", `08f3b68d` "Invoke custom commands from custom proc/txn (#597)", `a658d2a6` "Add NotFound callback..." — none add publish.
- Lua publish works because `SessionScriptCache.cs:61-64` explicitly constructs a scratch `RespServerSession` with the broker: `// Pass storeWrapper.subscribeBroker so Lua scripts can use publish-side Pub/Sub` → `new RespServerSession(0, scratchBufferNetworkSender, storeWrapper, storeWrapper.subscribeBroker, authenticator, false)`. This is in-repo proof the broker is the intended publish mechanism; it's just wired only into session/Lua internals.

### 3.3 HOST CAN reach the broker — public path via subclassing

Since Highway.Server hosts `GarnetServer` in-process, the doorbell IS implementable without reflection, using fully public types chained through one `protected` field:

| Link | Declaration | File:line |
|---|---|---|
| `protected StoreWrapper storeWrapper;` | protected field on `GarnetServer` | `libs/host/GarnetServer.cs:57` |
| `public sealed class StoreWrapper` | public class | `libs/server/StoreWrapper.cs:26` |
| `public readonly SubscribeBroker subscribeBroker;` | **public readonly field** | `libs/server/StoreWrapper.cs:90` |
| `public sealed class SubscribeBroker : IDisposable, ILogEntryConsumer` | public class | `libs/server/PubSub/SubscribeBroker.cs:19` |
| `public unsafe int PublishNow(PinnedSpanByte key, PinnedSpanByte value)` | synchronous broadcast, returns subscriber count | `SubscribeBroker.cs:297` |
| `public unsafe void Publish(PinnedSpanByte key, PinnedSpanByte value)` | async: enqueues to broker's TsavoriteLog; background loop broadcasts | `SubscribeBroker.cs:308` |

Pattern:

```csharp
sealed class HighwayGarnetServer : GarnetServer
{
    public HighwayGarnetServer(GarnetServerOptions opts, ILoggerFactory lf = null) : base(opts, lf) { }
    public SubscribeBroker Broker => storeWrapper.subscribeBroker; // valid immediately after ctor
}
// doorbell:
int n = broker.PublishNow(channelSlice, messageSlice); // from any thread
```

This is exactly what the server's own PUBLISH command does — `PubSubCommands.cs:134`: `numClients = subscribeBroker.PublishNow(key, value);` (wrapped with a `publishingThreadId` reentrancy guard for the self-subscribe case; that guard is only needed when the publisher is itself a subscribed session on the same thread).

Caveats verified in code:

- The broker is lazily initialized on the first `Subscribe` call (`Initialize()` at `SubscribeBroker.cs:~171` starts the consumer task and creates the subscription maps). `PublishNow` is safe before that (returns 0); the async `Publish` silently drops messages while `subscriptions == null && patternSubscriptions == null`.
- `PublishNow` calls `session.Publish(key, value)` (`ServerSessionBase.Publish`, `public abstract unsafe void Publish(PinnedSpanByte key, PinnedSpanByte value)` at `libs/server/Sessions/ServerSessionBase.cs:42`) which writes into each subscriber's network buffer under the sender lock — cross-thread broadcast is a supported pattern (the broker's own background consumer does it).
- `DisablePubSub = true` makes `subscribeBroker` null (GarnetServer.cs:247-248 creates it only when `!opts.DisablePubSub`); PUBLISH then returns `ERR PUBLISH is disabled...` (`PubSubCommands.cs:122-125`). Highway must leave pub/sub enabled.

**Other host-side surfaces checked and found insufficient:** `GarnetServer.subscribeBroker` is `private` (host, line 45); `GarnetServer.Provider` is `internal`; `GarnetProvider.StoreWrapper` is `internal` (`GarnetProvider.cs:24`); `StoreApi` exposes only `WaitForCommitAsync`/`CommitAOFAsync`/`FlushDB` (`StoreApi.cs`); there is **no class named `RespProvider`** in this checkout; `IServerHook` (`libs/common/Networking/IServerHook.cs`) only creates/disposes consumers. So the subclass route in 3.3 is the clean supported path; reflection into `GarnetServer.Provider`/`storeWrapper` is the fallback.

**Conclusion:** custom raw commands, transactions, and procedures have NO publish/notification mechanism reachable from external assemblies (verified, unchanged from older versions, no release-history evidence of addition). The doorbell must be rung from the host layer via the subclass-exposed `SubscribeBroker.PublishNow`.

---

## 4. LIST Operations via IGarnetApi

All in `libs/server/API/IGarnetApi.cs`. Write-side (`IGarnetApi`, which `Main<TGarnetApi>` receives):

```csharp
// Push (lines 807-865)
GarnetStatus ListLeftPush(PinnedSpanByte key, ref ObjectInput input, ref ObjectOutput output);
GarnetStatus ListLeftPush(PinnedSpanByte key, PinnedSpanByte element, out int count, bool whenExists = false);
GarnetStatus ListLeftPush(PinnedSpanByte key, PinnedSpanByte[] elements, out int count, bool whenExists = false);
GarnetStatus ListRightPush(PinnedSpanByte key, ref ObjectInput input, ref ObjectOutput output);
GarnetStatus ListRightPush(PinnedSpanByte key, PinnedSpanByte element, out int count, bool whenExists = false);
GarnetStatus ListRightPush(PinnedSpanByte key, PinnedSpanByte[] elements, out int count, bool whenExists = false);

// Pop (lines 878-942)
GarnetStatus ListLeftPop(PinnedSpanByte key, ref ObjectInput input, ref ObjectOutput output);
GarnetStatus ListLeftPop(PinnedSpanByte key, out PinnedSpanByte element);
GarnetStatus ListLeftPop(PinnedSpanByte key, int count, out PinnedSpanByte[] elements);
GarnetStatus ListLeftPop(PinnedSpanByte[] keys, int count, out PinnedSpanByte key, out PinnedSpanByte[] elements);
GarnetStatus ListRightPop(PinnedSpanByte key, ref ObjectInput input, ref ObjectOutput output);
GarnetStatus ListRightPop(PinnedSpanByte key, out PinnedSpanByte element);
GarnetStatus ListRightPop(PinnedSpanByte key, int count, out PinnedSpanByte[] elements);
GarnetStatus ListRightPop(PinnedSpanByte[] keys, int count, out PinnedSpanByte key, out PinnedSpanByte[] elements);

// Move / trim / insert / remove / set / position (lines 956-1000)
GarnetStatus ListMove(PinnedSpanByte sourceKey, PinnedSpanByte destinationKey, OperationDirection sourceDirection, OperationDirection destinationDirection, out byte[] element);
public bool ListTrim(PinnedSpanByte key, int start, int stop);
GarnetStatus ListTrim(PinnedSpanByte key, ref ObjectInput input);
GarnetStatus ListInsert(PinnedSpanByte key, ref ObjectInput input, ref ObjectOutput output);
GarnetStatus ListRemove(PinnedSpanByte key, ref ObjectInput input, ref ObjectOutput output);
GarnetStatus ListSet(PinnedSpanByte key, ref ObjectInput input, ref ObjectOutput output);
GarnetStatus ListPosition(PinnedSpanByte key, ref ObjectInput input, ref ObjectOutput output);
```

Read-side (`IGarnetReadApi`, declared at `IGarnetApi.cs:1354`, which `Prepare<TGarnetReadApi>` receives; methods at lines 1671-1698):

```csharp
GarnetStatus ListLength(PinnedSpanByte key, out int count);
GarnetStatus ListLength(PinnedSpanByte key, ref ObjectInput input, ref ObjectOutput output);
GarnetStatus ListRange(PinnedSpanByte key, ref ObjectInput input, ref ObjectOutput output);
GarnetStatus ListIndex(PinnedSpanByte key, ref ObjectInput input, ref ObjectOutput output);
```

Note: there are no `ListAddFor`/`ListMoveFor`/`ListRemoveFor` names in this version — the `*For` naming from older Garnet is gone; the plain names above are current. The simple `PinnedSpanByte`-overloads (e.g. `ListRightPush(key, element, out count)`, `ListLeftPop(key, out element)`, `ListLength(key, out count)`) are the practical ones for transactions; the `ref ObjectInput` overloads require building an `ObjectInput` with a `RespInputHeader(GarnetObjectType.List)`.

### Expiry / TTL from custom transactions — YES

`IGarnetApi` (`IGarnetApi.cs` lines 190-240):

```csharp
GarnetStatus EXPIRE(PinnedSpanByte key, PinnedSpanByte expiryMs, out bool timeoutSet, ExpireOption expireOption = ExpireOption.None);
GarnetStatus EXPIRE(PinnedSpanByte key, ref UnifiedInput input, ref UnifiedOutput output);
GarnetStatus EXPIRE(PinnedSpanByte key, TimeSpan expiry, out bool timeoutSet, ExpireOption expireOption = ExpireOption.None);
GarnetStatus EXPIREAT(PinnedSpanByte key, long expiryTimestamp, out bool timeoutSet, ExpireOption expireOption = ExpireOption.None);
GarnetStatus PEXPIREAT(PinnedSpanByte key, long expiryTimestamp, out bool timeoutSet, ExpireOption expireOption = ExpireOption.None);
GarnetStatus PERSIST(PinnedSpanByte key, ref UnifiedInput input, ref UnifiedOutput output);
GarnetStatus DELETE(PinnedSpanByte key);
GarnetStatus SETEX(PinnedSpanByte key, PinnedSpanByte value, TimeSpan expiry);
```

`IGarnetReadApi` has `TTL(PinnedSpanByte key, ref UnifiedInput input, ref UnifiedOutput output)` and `EXPIRETIME(...)` (lines ~1417-1443).

### Access pattern

A transaction accesses the API only through the generic parameters Garnet injects: `Prepare<TGarnetReadApi>(TGarnetReadApi api, ...)` (reads + `AddKey` declarations) and `Main<TGarnetApi>(TGarnetApi api, ...)`. Keys written in `Main` must be locked via `AddKey(key, LockType.Exclusive, StoreType.Main|Object)` during `Prepare` (see `ReadWriteTxn` below, §6). Transaction execution flow is in `TransactionManager.RunTransactionProc` (`libs/server/Transaction/TransactionManager.cs:~290-360`).

---

## 5. Reply Writing

### Store-function commands (raw string / object): `RespMemoryWriter`

`libs/common/RespMemoryWriter.cs` — `public unsafe ref struct RespMemoryWriter : IDisposable` wrapping `ref SpanByteAndMemory output`, auto-growing. Key methods:

```csharp
public RespMemoryWriter(byte respVersion, ref SpanByteAndMemory output);
public void WriteSimpleString(ReadOnlySpan<char> simpleString);   // +OK\r\n style
public void WriteSimpleString(ReadOnlySpan<byte> simpleString);
public void WriteError(ReadOnlySpan<char> errorString);           // -ERR ...\r\n
public void WriteError(scoped ReadOnlySpan<byte> errorString);
public void WriteInt64(long value);                               // :N\r\n
public void WriteInt32(int value);
public void WriteInt64AsBulkString(long value);
public void WriteBulkString(scoped ReadOnlySpan<byte> item);      // $len\r\n...\r\n
public void WriteAsciiBulkString(string chars);
public void WriteUtf8BulkString(ReadOnlySpan<char> chars);
public void WriteArrayLength(int len);                            // *N\r\n
public void WriteArrayItem(long item);
public void WriteNull(); public void WriteNullArray();
public void WriteMapLength(int len); public void WriteSetLength(int len); // RESP2/3 aware
public void Realloc(int totalLenHint);
public readonly ReadOnlySpan<byte> AsReadOnlySpan();
```

An array of bulk strings = `WriteArrayLength(n)` then `WriteBulkString(...)` per item. The writer is RESP2/RESP3-aware (`resp3` field).

If an RMW/read updater writes nothing, the server sends `+OK` by default — see `TryCustomRawStringCommand` in `libs/server/Custom/CustomRespCommands.cs`: `while (!RespWriteUtils.TryWriteDirect(CmdStrings.RESP_OK, ref dcurr, dend)) SendAndReset();` (same for transactions/procedures in `TryTransactionProc`/`TryCustomProcedure`).

### Transactions / procedures: `MemoryResult<byte>` + `CustomProcedureBase` helpers

Output param is `ref MemoryResult<byte> output` (`MemoryOwner` + `Length`). Use the inherited static helpers (full bodies in `libs/server/Custom/CustomProcedureBase.cs`):

```csharp
WriteSimpleString(ref output, "SUCCESS");            // rents from MemoryPool<byte>.Shared, writes +SUCCESS\r\n
WriteError(ref output, "ERR something");             // -ERR something\r\n
WriteBulkString(ref output, span);                   // $len\r\n...\r\n
WriteBulkStringArray(ref output, slice1, slice2);    // *2\r\n$..\r\n$..\r\n
```

Underlying primitives are `RespWriteUtils.TryWriteSimpleString/TryWriteBulkString/TryWriteArrayLength/TryWriteError` (`libs/common/RespWriteUtils.cs`). A failed transaction returns `output` if written, else `-ERR Transaction failed.` (`CustomRespCommands.cs`, `TryTransactionProc`).

---

## 6. Samples & Tests

### In-repo sample implementations (model these)

Directory `main/GarnetServer/Extensions/` (14 files):

- `SetIfPM.cs` — `sealed class SetIfPMCustomCommand : CustomRawStringFunctions` (full RMW override set; note `// +OK is sent as response, by default`)
- `ReadWriteTxn.cs` — `sealed class ReadWriteTxn : CustomTransactionProcedure` (Prepare does `api.GET(...)` + `AddKey(GetNextArg(...), LockType.Exclusive, StoreType.Main)`; Main does `api.GET`/`api.SET` + `WriteSimpleString(ref output, "SUCCESS")`)
- `Sum.cs` — `class Sum : CustomProcedure` (Execute with `GetNextArg` loop)
- `MyDictObject.cs` / `MyDictSet.cs` / `MyDictGet.cs` — custom object factory + RMW/Read object commands
- `SetStringAndList.cs` — procedure touching main store + object store (list)
- `MSetPx.cs`, `MGetIfPM.cs`, `GetTwoKeysNoTxn.cs` — multi-key locking patterns
- `SampleUpdateTxn.cs`, `SampleDeleteTxn.cs`, `DeleteIfMatch.cs`, `SetWPIfPGT.cs`

Module samples: `playground/SampleModule/SampleModule.cs` (registers command + transaction + type + procedure in `OnLoad`); production modules: `modules/GarnetJSON/JsonModule.cs`, `modules/RoaringBitmap`, `modules/NoOpModule`.

### Test fixtures registering programmatically

Project `test/standalone/Garnet.test.scripting/`:

- `RespCustomCommandTests.cs` — e.g. line 289: `int x = server.Register.NewCommand("SETIFPM", CommandType.ReadModifyWrite, new SetIfPMCustomCommand(), new RespCommandsInfo { Arity = 4 });`; object-command registration at lines 565-566 (`NewCommand("MYDICTSET", CommandType.ReadModifyWrite, factory, new MyDictSet(), ...)`)
- `RespTransactionProcTests.cs` — line 36: `server.Register.NewTransactionProc("READWRITETX", () => new ReadWriteTxn(), new RespCommandsInfo { Arity = 4 });`
- `RespModuleTests.cs` — line 317: `server.Register.NewModule(new NoOpModule.NoOpModule(), [], out _);`
- `RespAofTests.cs` — AOF recovery tests with custom commands/procedures (lines 715-760, 1079-1144); registration happens BEFORE `Start()` on the recovering server.

### Embedded server + ports

`test/standalone/Garnet.test/TestUtils.cs`:

- `CreateGarnetServer(...)` (line 285) builds `GarnetServerOptions` and returns `new GarnetServer(opts, loggerFactory)` (line 529).
- **Tests do NOT use `Port = 0`.** Each test project gets a fixed dedicated port via `enum TestPortAssignment` (lines 82-96): `GarnetTest = 33278`, `GarnetTestScripting = 34800`, etc., selected by `SetTestPort` in a `[SetUpFixture]` (e.g. `test/standalone/Garnet.test.scripting/TestProjectSetup.cs`).
- **Ephemeral binding gap (FLAGGED):** `GarnetServerTcp.Start()` (`libs/server/Servers/GarnetServerTcp.cs:138-148`) does `listenSocket.Bind(EndPoint)` — an `IPEndPoint` with port 0 would bind an OS-assigned port, but `listenSocket` is private and neither `IGarnetServer` nor `GarnetServer` exposes the resulting local endpoint. So `Port = 0` works at the socket level but the actual port cannot be read back through any public API; Highway needs a fixed port, its own probe, or a custom `IGarnetServer[]` passed to the constructor.

---

## 7. Hosting (`GarnetServerOptions`)

`libs/server/Servers/GarnetServerOptions.cs` (+ base `ServerOptions` in the same directory):

| Concern | Property (default) | File |
|---|---|---|
| Port/endpoint | `EndPoint[] EndPoints` (`[IPEndPoint(IPAddress.Loopback, 6379)]`) | ServerOptions.cs:20 |
| Memory-only vs disk | `bool EnableStorageTier = false` + `string LogDir = null`; `string CheckpointDir = null` | ServerOptions.cs |
| Memory sizing | `LogMemorySize = "16g"`, `PageSize = "16m"`, `IndexMemorySize = "128m"`, `MutablePercent = 90`, `IndexMaxMemorySize` | ServerOptions.cs |
| AOF | `bool EnableAOF = false`, `int CommitFrequencyMs = 0` (0 = commit per op, -1 = manual COMMITAOF), `bool WaitForCommit = false`, `AofMemorySize = "128m"`, `AofPageSize = "32m"`, `AofSizeLimit`, `AofReplayTaskCount`, `FastAofTruncate` | GarnetServerOptions.cs |
| Checkpointing | `Recover = false`, `FailOnRecoveryError`, `FullCheckpointLogInterval = 1<<30`, `UseFoldOverCheckpoints`, `CheckpointThrottleFlushDelayMs`, `CompactionType` | both files |
| Pub/sub | `bool DisablePubSub = false`, `string PubSubPageSize = "4k"` | ServerOptions.cs |
| Misc hosting | `QuietMode`, `DisableObjects`, `EnableCluster = false`, `LoadModuleCS`, `ExtensionBinPaths`, `ExtensionAllowUnsignedAssemblies`, `EnableModuleCommand`, `AuthSettings`, `TlsOptions`, `MaxDatabases = 16`, `ThreadPoolMin/MaxThreads`, `NetworkConnectionLimit` | GarnetServerOptions.cs |

Validation enforced in `GarnetServer.CreateAOF` (GarnetServer.cs:~470): `CommitFrequencyMs != 0 || WaitForCommit` without `EnableAOF` throws `"Cannot use CommitFrequencyMs or CommitWait without EnableAOF"`; `WaitForCommit` with manual commits (`CommitFrequencyMs < 0`) throws.

Lifecycle: `Start()` (runs `Provider.RecoverAsync()` synchronously, then starts listeners); `Dispose()` / `Dispose(bool deleteDir)` — `InternalDispose` (GarnetServer.cs:~530) phases: close listeners (frees ports), drain handlers, dispose provider, `subscribeBroker?.Dispose()`. `GarnetServer(GarnetServerOptions, ...)` constructor performs full initialization without touching config files, so registration can happen immediately after construction.

---

## 8. AOF & Durability of Custom Commands

### Read vs write determination

Solely the `CommandType` value passed at registration — `libs/server/Custom/CommandType.cs`:

```csharp
public enum CommandType : byte { Read, ReadModifyWrite }
```

No attributes, no CommandInfo flag inference. Dispatch (`CustomRespCommands.cs`, `TryCustomRawStringCommand`): `CommandType.ReadModifyWrite` → `storageApi.RMW_MainStore(key, ref input, ref output)`; `CommandType.Read` → `storageApi.Read_MainStore(...)`. Cluster read-only determination uses exactly this: `RespServerSessionSlotVerify.cs:69-70`: `var isReadOnly = cmd == RespCommand.CustomRawStringCmd ? currentCustomRawStringCommand.type == CommandType.Read : ...`.

### Raw-string RMW commands are automatically AOF-logged

The `StringInput` header carries the custom command's `RespCommand` id + the parsed args. When the RMW/upsert mutates, the main-store Tsavorite functions log it: `libs/server/Storage/Functions/MainStore/PrivateMethods.cs`:

- `WriteLogRMW(...)` (line ~793): `functionsState.appendOnlyFile.Log.Enqueue(AofEntryType.StoreRMW, version, sessionId, key, ref input, ...)`
- `WriteLogUpsert(...)` (line ~762), `WriteLogDelete(...)` (line ~828)

Both early-return when `functionsState.StoredProcMode` is set (i.e., inside a custom transaction — see below). Replay goes through `AofProcessor.cs` (`case AofEntryType.StoreRMW:` at line ~471), which deserializes the `StringInput` and re-executes through the same store path — so the custom command must be registered before `Start()` during recovery (verified test pattern, `RespAofTests.cs:749-752`). No special registration flag is needed for logging; being `ReadModifyWrite` and mutating is sufficient.

### Custom transactions are logged as one stored-procedure entry

`libs/server/Transaction/TransactionManager.cs`, `RunTransactionProc`:

1. `functionsState.StoredProcMode = true;` (line ~309) — suppresses per-write AOF records inside `Main`.
2. `proc.Main(...)` runs under locks.
3. `Log(id, ref procInput, proc)` (line ~338) → line 370-377:

```csharp
if (PerformWrites && appendOnlyFile != null)
    appendOnlyFile.Log.EnqueueStoredProc(AofEntryType.StoredProcedure, id, txnVersion,
        stringBasicContext.Session.ID, ref procInput, proc);
```

4. `Commit()`; `Finalize` runs afterwards and is skipped during AOF replay (doc comment in `CustomTransactionProcedure.cs`; replay path `RunCustomTxnProcAtReplica(..., isRecovering: true)` in `CustomRespCommands.cs`).

So: the whole transaction (proc id + arguments) is replayed atomically; individual ops inside `Main` are NOT double-logged.

### Custom procedures (`CustomProcedure`)

Not logged as a unit (they issue ordinary `IGarnetApi` calls, each logged via the normal store-function paths above). `CustomProcedureBase.ExecuteCustomRawStringCommand`/`ExecuteCustomObjectCommand` let procedures/transactions invoke other registered custom commands (`CustomRespCommands.cs:InvokeCustomRawStringCommand`).

---

## Summary of Flags (ABSENT / does not work)

1. `CustomCommandRegistry`, `RegisterCmd`, `RegisterCustomCommand`, `CustomRawCommandBase`, `MainAsync` — all ABSENT in v2.1.2; replaced by `RegisterApi`/`ModuleBase`.
2. `IGarnetApi`/`IGarnetReadApi`/`IGarnetAdvancedApi` publish/subscribe surface — ABSENT (zero matches in `libs/server/API` and `libs/host`); no new support added per git history; no CHANGELOG/RELEASENOTES file exists to suggest otherwise.
3. Custom command → broker/session access from external assemblies — ABSENT (`respServerSession`/`txnManager` are `internal`; `RespServerSession.subscribeBroker` is private; `InternalsVisibleTo` excludes Highway).
4. Host-side broker access without subclassing — ABSENT via public API (`GarnetServer.subscribeBroker` private, `Provider` internal, `GarnetProvider.StoreWrapper` internal, no `RespProvider` class); the `protected StoreWrapper storeWrapper` subclass route is the one clean path, and `SubscribeBroker.PublishNow`/`Publish` are public.
5. Ephemeral port (`Port = 0`) readback — no public API to discover the OS-assigned port after bind; tests use fixed per-project ports.
6. `WaitForCommit`/`CommitFrequencyMs` without `EnableAOF`, or `WaitForCommit` with manual commits (`CommitFrequencyMs = -1`) — throws at construction.

---

## Impact on Highway Feature 004

These findings directly determine the 004 design (see `design.md` in this directory):

| Finding | Design consequence |
|---|---|
| `RegisterApi.NewTransactionProc` is the registration path | All nine HW.* commands are `CustomTransactionProcedure`s |
| Transactions replay as one atomic AOF entry; `Finalize` skipped at recovery | Atomicity + durability guarantees met; doorbells placed in `Finalize` so recovery never re-rings them |
| Custom code cannot publish | Doorbells rung from host layer via `HighwayGarnetServer` subclass → `storeWrapper.subscribeBroker.PublishNow` |
| Ephemeral port not readable | Task 1 spike: custom `IGarnetServer` wrapper vs. port probe for `HighwayTestServer` |
| Registration must precede `Start()` for recovery | `HighwayServerBuilder.Build()` registers before `Start()` |
| List/set/EXPIRE primitives available in transactions | Queues, processing lists, subscriber sets, reply-slot TTLs all implementable server-side |
