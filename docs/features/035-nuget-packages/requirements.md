# Feature: NuGet Packages — Highway as a Dependency

## Introduction

`product.md` has said from the beginning: *"The client is a NuGet package."* It is not. There
is **no package metadata anywhere** — no `PackageId`, `Version`, `Description`, `Authors`,
licence or repository URL in any of the four `src/` projects. A developer who reads the
UserGuide, likes what they see and types `dotnet add package Highway.Client` gets nothing.
The only way to use Highway today is to clone it, initialise a submodule, and reference the
projects.

Feature 031 makes the **broker** downloadable — a versioned zip published on GitHub Releases,
installed as a Windows service or a systemd daemon. This feature makes the **libraries**
installable. Two channels, two audiences, no overlap:

| Channel | Carries | For |
|---|---|---|
| **GitHub Releases** | the `highways` distribution zip (031) | operators deploying a broker |
| **NuGet** | `Highway.Abstractions`, `Highway.Client`, `Highway.LocalServer` | developers building against Highway, and running one in-process for tests and local development |

The `Highway.LocalServer` package is deliberately part of the developer story rather than the
deployment story: it carries `HighwayTestServer` and the embedded `HighwayServerBuilder`, so
integration tests and a local run need no infrastructure at all. Production still deploys the
zip.

**Lane:** connective tissue. It closes the gap between a stated delivery model and the
delivery. No verb, no command, no protocol surface.

### The blocker, and why it is not one

Packaging `Highway.Server` looked impossible: it references Garnet from **source**
(`libs/garnet/libs/host`, `libs/server`, `Tsavorite.core`), and a NuGet package cannot carry
a `ProjectReference`. The reason recorded in its `.csproj` is that
`Garnet.host` marks its own dependencies `PrivateAssets="All"`, so Highway referenced them
directly to reach `GarnetServerOptions`, `SubscribeBroker` and `PinnedSpanByte`.

**Verified 2026-08-18: the `Microsoft.Garnet` package ships all of them anyway.**
`Garnet.host.csproj` packs its private dependencies into `lib/{tfm}` by content include, and
the restored `microsoft.garnet/2.1.3` package contains ten assemblies in `lib/net10.0`,
including `Garnet.server.dll` and `Tsavorite.core.dll`. Everything in `lib/` is a compile-time
reference for consumers, so the types Highway needs are reachable from the package. The
source submodule is a convenience, not a requirement.

## Decisions

| # | Decision | Resolution |
|---|---|---|
| **D1** | Which projects pack | **Three**: `Highway.Abstractions`, `Highway.Client`, and `Highway.LocalServer` (the `Highway.Server` project, packaged under a name that states its purpose — *revised 2026-08-18 by the user*). `Highway.Server.Host` and `Highway.Server.Dashboard` stay `IsPackable=false` |
| **D2** | How `Highway.Server` gets Garnet | **`PackageReference Microsoft.Garnet`**, replacing the three source `ProjectReference`s. Verified to expose every type Highway compiles against |
| **D3** | Does the submodule stay? | **Yes, as an opt-in build mode.** Default build uses the package; a property switches to source for debugging into Garnet or carrying a patch. Dropping it would have discarded the one lever available for C4.6 |
| **D4** | The dashboard is **not packaged at all** | *(Revised 2026-08-18 by the user; it was previously to ship as its own package.)* It runs inside the broker process where it reads the flight recorder live (031 OD9), so it ships in the `highways` distribution. A package would serve only someone hosting a broker themselves — the shape 031 exists to replace — and would pull the ASP.NET Core shared framework into every test host that referenced it |
| **D5** | Version scheme | **`1.0.0-preview.N`** while pre-1.0 — NuGet treats it as prerelease, and the roadmap already defines what 1.0 means, so `0.x` would say something less specific |
| **D6** | Strong naming | **No.** Garnet strong-names its own assemblies, which is what matters for referencing them; nothing requires Highway to. Adding a key is a one-way door |
| **D7** | Publishing | **Manual `dotnet nuget push` from a documented command**, same posture as 031's zip. A GitHub Actions release pipeline is registered as deferred, not built here |

## Open Question

**The package IDs and the nuget.org account.** `Highway.Client` and `Highway.Abstractions`
are generic identifiers that may already be taken, and nuget.org supports ID prefix
reservation. This needs an answer before anything is pushed, and only the owner can give it:

- Which nuget.org account/organisation publishes, and is `Highway.*` available or reservable?
- If not, the fallback is a prefix — `SystemD.Highway.Client` and friends — which is a naming
  decision with a documentation cost, since every code sample and guide names the package.

Everything else in this feature is decided. Nothing here is blocked on the answer except the
final push.

## Requirements

### Requirement 1: Three Packages, Properly Described

**User Story:** As a .NET developer, I want to add Highway with `dotnet add package`, so that
trying it costs a command instead of a clone.

#### Acceptance Criteria

1. Three projects produce packages (D1). `Highway.Server.Host` and `Highway.Server.Dashboard` produce none — the first is the
   executable, and its channel is 031's zip
2. Every package carries complete metadata: id, version, authors (**System D AB**),
   description, licence expression (`MIT`), project URL, repository URL and commit, tags, and
   a per-package `README.md` that renders on nuget.org. A package whose nuget.org page is
   blank is a package nobody installs
3. Dependencies are declared, not bundled: `Highway.Client` depends on
   `Highway.Abstractions`; `Highway.LocalServer` on `Highway.Abstractions` and `Microsoft.Garnet`.
   No assembly is copied into a package that
   a dependency already provides
4. `Highway.Abstractions` keeps **zero package dependencies**. That property is what lets a
   shared contracts assembly be referenced by both sides of a system without pulling an
   engine in, and it is asserted by a test rather than assumed
5. Each package restores and compiles in a scratch project outside this repository — the
   proof is a consumer, not a `.nupkg` that exists

### Requirement 2: Garnet From the Package, Not From Source

**User Story:** As a maintainer, I want the server library to build without a submodule, so
that it can be packaged and so a clone is one command.

#### Acceptance Criteria

1. `Highway.Server` references `Microsoft.Garnet` by `PackageReference` and drops the three
   source `ProjectReference`s (D2). It compiles, and the full suite passes — the same 948
   tests, unchanged
2. The referenced package version is **pinned in `Directory.Packages.props`** with the other
   central versions, and the choice is justified in one line: which Garnet release, and that
   it contains what the currently pinned submodule commit contains. A silent version drop is
   the failure mode this requirement exists to prevent — the submodule sits at a commit ahead
   of a release, and moving to a package must not lose a fix
3. The submodule remains available as an **opt-in build mode** (D3): an MSBuild property
   switches `Highway.Server` back to source references for debugging into Garnet or carrying
   a local patch. Documented, and exercised at least once so it is known to work
4. A clone **without** `--recursive` builds and tests successfully in the default mode. That
   is the observable proof the dependency changed
5. The README, the samples and 031's documentation stop requiring `--recursive` for the
   default path, and say when it is still needed

### Requirement 3: One Version, Everywhere

**User Story:** As an operator holding a broker zip and a developer holding a package, I want
to know whether they match.

#### Acceptance Criteria

1. A **single central version property** drives every package version, the assembly
   informational version, `highways --version` and 031's zip folder name. It does not exist
   today — 031's T4 flagged its absence, and this feature is where it lands
2. The scheme is `1.0.0-preview.N` while pre-1.0 (D5), and how to bump it is documented in
   one place
3. `highways --version` and the package version are the same string for a given commit, so
   "which broker goes with which client" is answerable without a changelog
4. Repository URL and **commit SHA** are embedded in every package, so any assembly can be
   traced back to the source that produced it

### Requirement 4: Debuggable and Verifiable

**User Story:** As a developer stepping into a Highway call, I want source and symbols, so
that a problem in my code does not look like a problem in a black box.

#### Acceptance Criteria

1. Every package ships a **symbol package** (`.snupkg`) and enables **SourceLink**, so a
   consumer can step into Highway source from their debugger
2. **XML documentation is generated and packaged.** `GenerateDocumentationFile` is set
   nowhere today, so the doc comments — which are unusually detailed, and are the product's
   in-editor documentation — never reach a consumer's IntelliSense. This requirement is the
   cheapest documentation win available
3. Builds are **deterministic** (`ContinuousIntegrationBuild` in CI), so the same source and
   version produce the same package
4. Packing produces **no NuGet warnings**. `dotnet pack` warnings are the class of problem
   that only appears on someone else's machine

### Requirement 5: Two Channels, Stated Once

**User Story:** As someone deciding how to get Highway, I want the difference between the
Release zip and the NuGet packages explained where I am looking.

#### Acceptance Criteria

1. The README and the UserGuide state the split: **Releases** carries the broker
   distribution for deployment; **NuGet** carries the libraries for development, including an
   in-process broker for tests. Each says what the other is for
2. `Highway.LocalServer`'s package README is explicit that it is the **in-process development**
   server — running a production broker means the distribution from Releases (031), not a
   library reference in a host you write. That is precisely the confusion 031 exists to end,
   and a package can re-create it
3. The getting-started path in the README changes from "clone the repository" to
   "`dotnet add package`", with the clone kept for contributors
4. Every package's README links to the guide, the protocol file and `constraints.md`, so a
   developer who arrives via nuget.org reaches the same honest documentation as one who
   arrives via GitHub

### Requirement 6: Publishing Is Repeatable

**User Story:** As the maintainer, I want a documented command that produces every package,
so that a release is a procedure rather than a memory.

#### Acceptance Criteria

1. **One documented command** packs all four from a clean checkout into an output directory,
   with the version supplied once
2. Publishing is a second documented command (D7): `dotnet nuget push` for the packages and
   their symbol packages. Manual and deliberate, matching 031's posture on the zip
3. A prerelease can be pushed and consumed end to end before any 1.0 exists — the flow is
   proven, not designed
4. Automating publication in CI is **out of scope and registered as deferred**, with signing
   and tagging policy, alongside 031's container-registry deferral

### Requirement 7: The Record

**User Story:** As a Highway maintainer, I want this held to the project's standard.

#### Acceptance Criteria

1. `docs/HIGHWAY-PROTOCOL.md` is **not modified**
2. `product.md`'s package-architecture section marks the three-package delivery as real and
   names the fourth, correcting a claim that has been aspirational since the beginning
3. `roadmap.md` records 035 and its relationship to 031 — two channels, one version
4. All tests pass; `dotnet build --no-incremental` warning-free; the samples run and the
   RUNLOG records it, since `Highway.Server`'s dependency shape changes underneath them

## Non-Goals

- **Publishing to a registry automatically.** Deferred with 031's container-registry item.
- **Strong naming or Authenticode signing.** D6; revisit only if a consumer requires it.
- **Packaging the `highways` executable as a dotnet tool.** A broker is not a tool; its
  channel is the Release zip. If a `dotnet tool` ever makes sense it will be for client-side
  tooling, which does not exist yet.
- **Multi-targeting.** `net10.0` only, as today. Adding target frameworks is a support
  commitment, not a build flag.
- **A NuGet package for the assurance rig or samples.** They are not libraries.

## Cross-References

- `docs/features/031-server-distribution/` — the other channel; the version property in R3
  is the one 031's T4 needs, and R5.2 exists to keep the two stories from blurring
- `docs/features/026-distributed-cache/` — `Highway.Client`'s cache dependencies, which
  become package dependencies here
- `docs/product/product.md` § Delivery (Package Architecture) — the claim R7.2 corrects
- `docs/product/UserGuide.md` — the getting-started path R5.3 changes
