# Feature 035 — NuGet Packages: Tasks

**T1 comes first because it is the only task that can fail the feature.** Everything else is
metadata and documentation; the Garnet dependency swap is the one change that could turn out
to be impossible or unsafe, and finding that out after four packages have been described is
the wrong order. If T1 cannot be completed safely, the feature ships three packages instead
of four and says why.

## Phase 1 — the dependency swap

### - [x] T1 — `Highway.Server` takes Garnet from the package

*Requirements:* R2.1–R2.5
**Done when:**

1. **Version parity is established first**, before any reference changes. The submodule sits
   at a commit titled *"Harden LightEpoch: make the epoch announce part of the slot-claim
   CAS (#2015)"* — a concurrency fix — with `Version.props` reading `2.1.2`, while the
   available package is `2.1.3`. Confirm the chosen package version **contains that commit**.
   If it does not, stop: keep the source references, record the finding here, and ship three
   packages. A package that silently lacks a concurrency fix would not show up as a compile
   error, and that is the worst outcome this feature could produce.
2. The three source `ProjectReference`s are replaced by `PackageReference Microsoft.Garnet`,
   version pinned in `Directory.Packages.props` with a one-line justification.
3. The **full suite passes** — the same 948 tests, unchanged. Not just the build: unsafe code
   in `DoorbellBridge` pins byte arrays over Garnet types, and "should be identical" is why
   the tests run rather than the compiler alone.
4. `-p:UseGarnetSource=true` restores the source path (D3) and the suite passes that way too,
   exercised once so the escape hatch is known to work rather than assumed.
5. A clone **without** `--recursive` builds and tests successfully in the default mode — the
   observable proof the dependency actually changed.

## Phase 2 — packaging

### - [x] T2 — Central version and metadata

*Requirements:* R1.2, R3.1–R3.4
*Depends on:* T1
**Done when:** `Directory.Build.props` carries the version property and the shared metadata
from the design — authors and copyright **System D AB**, `MIT` licence expression, project
and repository URL, `PublishRepositoryUrl`; the version property drives package versions, the
assembly informational version, `highways --version` **and** 031's zip folder name, so the
two features cannot disagree about what a build is called (031 T4 flagged this absence and
this is where it lands); the scheme is `1.0.0-preview.N` and how to bump it is documented in
one place.

### - [x] T3 — Per-project package metadata and READMEs

*Requirements:* R1.1–R1.4, R5.2, R5.4
*Depends on:* T2
**Done when:** the four projects pack and `Highway.Server.Host` does not; each has its own
`Description`, `PackageTags` and `PackageReadmeFile`; the four READMEs are written for
someone who arrived at *that package* rather than copied from the repository README. The
`Highway.Server` README states plainly that it is the **embedded and development** server and
that production deploys the distribution from Releases — 031 exists because "run the broker"
used to mean "write a host", and a package can re-create exactly that confusion. Every README
links to the UserGuide, the protocol file and `constraints.md`.

### - [x] T4 — Symbols, SourceLink, XML docs, determinism

*Requirements:* R4.1–R4.4
*Depends on:* T2
**Done when:** every package ships a `.snupkg` with SourceLink working (a consumer can step
into Highway source); `GenerateDocumentationFile` is on and the XML ships — it is set nowhere
today, so the doc comments that explain the Send/Publish/Execute choice and at-least-once
semantics never reach a consumer's IntelliSense, and turning it on is the cheapest
documentation win in the repository; any missing-comment warnings on public members are
triaged rather than suppressed wholesale; `ContinuousIntegrationBuild` is set in CI; **`dotnet
pack` emits zero warnings**.

## Phase 3 — proof

### - [x] T5 — A consumer outside this repository

*Requirements:* R1.5, R1.4
*Depends on:* T3, T4
**Done when:** `build/verify/` holds a project **outside the solution** with a `nuget.config`
pointing at a local feed; it references `Highway.Client` and `Highway.Server`, starts a
`HighwayTestServer`, registers `AddHighway`, and round-trips a message — restore, compile,
run. This catches what a `.nupkg` existing does not prove: a missing transitive dependency, an
assembly that never got packed, a `lib/` for the wrong framework, a `PrivateAssets` that hid
something needed. A second assertion reads the packed `.nuspec` and requires
`Highway.Abstractions`' dependency group to be **empty** — the property that lets a contracts
assembly sit between two services without dragging an engine into either.

### - [x] T6 — Pack and push, end to end

*Requirements:* R6.1–R6.4
*Depends on:* T5
**Done when:** one documented command packs all four from a clean checkout with the version
supplied once; a second pushes packages and symbol packages; **a prerelease is actually
pushed and consumed from nuget.org** before any 1.0 exists — the flow is proven, not
designed. Automated publication stays out of scope, registered as deferred beside 031's
container-registry item.

*Blocked on the open question in `requirements.md`*: which nuget.org account publishes, and
whether `Highway.*` is available or needs a `SystemD.` prefix. Everything before this task
proceeds without the answer; this one cannot.

## Phase 4 — the record

### - [x] T7 — Two channels, said once, where people look

*Requirements:* R5.1, R5.3
*Depends on:* T3
**Done when:** the README and the UserGuide state the split — **Releases** carries the broker
distribution for deployment, **NuGet** carries the libraries for development including an
in-process broker for tests — each naming what the other is for; the getting-started path
changes from "clone the repository" to `dotnet add package`, with the clone kept for
contributors; the `--recursive` instruction is removed from the default path and kept where
it still applies.

### - [x] T8 — `product.md`, `roadmap.md`, samples, everything green

*Requirements:* R7.1–R7.4
*Depends on:* T6, T7
**Done when:** `product.md`'s package-architecture section marks the delivery real and names
the fourth package, correcting a claim that has been aspirational since the project began;
`roadmap.md` records 035 and its relationship to 031 — two channels, one version; the samples
run and `samples/RUNLOG.md` records it, because `Highway.Server`'s dependency shape changed
underneath them; full suite green; `dotnet build --no-incremental` warning-free;
`docs/HIGHWAY-PROTOCOL.md` byte-identical to before the feature.

---

**Order:** T1 → T2 → (T3 ∥ T4) → T5 → T6 ∥ T7 → T8.

**Deferred (registered, not built):**

- **Automated publishing from CI** — tagging policy, secret handling, and a signing decision.
  Release engineering, deferred beside 031's container-registry item.
- **Strong naming and Authenticode signing** — D6. A one-way door; revisit only when a
  consumer requires it, and record who.
- **Multi-targeting** — `net10.0` only. Additional target frameworks are a support
  commitment, not a build flag.
- **A `dotnet tool`** — a broker is not a tool. If one ever makes sense it will be client-side
  tooling, which does not exist yet.
