# Feature 035 — NuGet Packages: Design

## The package graph

```
Highway.Abstractions            no package dependencies — the property that makes it safe
   ▲          ▲                 for a shared contracts assembly to reference
   │          │
Highway.Client        Highway.LocalServer ──► Microsoft.Garnet (PackageReference)
   │                    (the Highway.Server    └─ lib/net10.0 carries Garnet.server,
   ├─ StackExchange.Redis    project)             Tsavorite.core and eight more
   └─ Microsoft.Extensions.*                      → the types Highway compiles against

Highway.Server.Dashboard  ← IsPackable=false. Ships inside the `highways` zip (031 OD9).
Highway.Server.Host       ← IsPackable=false. The `highways` executable; ships in 031's zip.
```

Three packages. Neither the executable nor the dashboard is one of them — both ship through
GitHub Releases, and R5.2 exists so a package reference is never mistaken for a deployment.

---

## Decisions

**D1 — the server packs as `Highway.LocalServer`, so the ID says what it is for.**
It carries `HighwayTestServer` and the embedded `HighwayServerBuilder`, which is how
integration tests run with no infrastructure — the thing that makes Highway pleasant to test
against. It is not the production deployment path. Feature 031 exists because "run the broker"
previously meant "write a host"; a package that invites people back into writing their own
host would re-create exactly that. One paragraph on nuget.org prevents it.

**D2 — Garnet moves from three `ProjectReference`s to one `PackageReference`.**
The recorded reason for referencing source was that `Garnet.host` marks its dependencies
`PrivateAssets="All"`, so `GarnetServerOptions`, `SubscribeBroker` and `PinnedSpanByte` would
not flow. That reasoning was right about the *reference* and wrong about the *package*:
`Garnet.host.csproj` packs its private dependencies into `lib/{tfm}` with an explicit content
include —

```xml
<Content Include="..\host\bin\Release\net10.0\*.dll"
         Exclude="..\host\bin\Release\net10.0\Garnet.host.dll"
         Pack="true" PackagePath="lib\net10.0" />
```

— and the restored `microsoft.garnet/2.1.3` package contains ten assemblies in `lib/net10.0`,
`Garnet.server.dll` and `Tsavorite.core.dll` among them. Assemblies in `lib/` are compile-time
references for consumers, so everything Highway uses is reachable. Verified by inspection of
the package on disk, not inferred.

**D3 — The submodule survives as an opt-in build mode.**

```xml
<PropertyGroup>
  <UseGarnetSource Condition="'$(UseGarnetSource)' == ''">false</UseGarnetSource>
</PropertyGroup>

<ItemGroup Condition="'$(UseGarnetSource)' == 'true'">
  <ProjectReference Include="..\..\libs\garnet\libs\host\Garnet.host.csproj" />
  <ProjectReference Include="..\..\libs\garnet\libs\server\Garnet.server.csproj" />
  <ProjectReference Include="..\..\libs\garnet\libs\storage\Tsavorite\cs\src\core\Tsavorite.core.csproj" />
</ItemGroup>

<ItemGroup Condition="'$(UseGarnetSource)' != 'true'">
  <PackageReference Include="Microsoft.Garnet" />
</ItemGroup>
```

`dotnet build -p:UseGarnetSource=true` restores the source path. Keeping it costs one
conditional and preserves two things worth preserving: stepping into Garnet while debugging,
and the ability to carry a patch — which is the only lever available for C4.6 if 034's
segment-size experiment fails.

**D4 — The dashboard is its own package.**
`FrameworkReference Microsoft.AspNetCore.App` is not a dependency you give someone by
accident. A console application running `HighwayTestServer` in its integration tests should
not acquire the ASP.NET Core shared framework because the dashboard exists.

**D5 — `1.0.0-preview.N`.**
NuGet treats it as prerelease and hides it from default listings, which is the correct
posture for software whose own README lists two unmet storage constraints. `0.x` would be
vaguer: the roadmap already defines what 1.0 contains, so the version can point at it.

**D6 — No strong naming.**
Highway references strong-named Garnet assemblies, which works regardless of Highway's own
signing — the constraint runs the other way. Introducing a key is a one-way door: consumers
bind to the identity, and removing it later breaks them. Revisit only if a consumer requires
it, and record the requirement when they do.

**D7 — Manual push.**
`dotnet nuget push` from a documented command, matching 031's posture on the zip. An
automated release pipeline needs a tagging policy, secret handling and a signing decision —
that is release engineering, registered as deferred beside 031's container-registry item.

---

## Central Metadata

Package metadata lives once, in `Directory.Build.props`, rather than four times:

```xml
<PropertyGroup>
  <VersionPrefix>1.0.0</VersionPrefix>
  <VersionSuffix>preview.1</VersionSuffix>          <!-- dropped at 1.0 -->

  <Authors>System D AB</Authors>
  <Company>System D AB</Company>
  <Copyright>Copyright (c) System D AB</Copyright>
  <PackageLicenseExpression>MIT</PackageLicenseExpression>
  <PackageProjectUrl>https://github.com/System-D-AB/highway</PackageProjectUrl>
  <RepositoryUrl>https://github.com/System-D-AB/highway</RepositoryUrl>
  <RepositoryType>git</RepositoryType>
  <PublishRepositoryUrl>true</PublishRepositoryUrl>

  <GenerateDocumentationFile>true</GenerateDocumentationFile>
  <IncludeSymbols>true</IncludeSymbols>
  <SymbolPackageFormat>snupkg</SymbolPackageFormat>
  <EmbedUntrackedSources>true</EmbedUntrackedSources>
  <ContinuousIntegrationBuild Condition="'$(CI)' == 'true'">true</ContinuousIntegrationBuild>
</PropertyGroup>
```

`Description`, `PackageTags` and `PackageReadmeFile` stay per-project, because they are the
only metadata that genuinely differs.

**`GenerateDocumentationFile` is set nowhere today.** Highway's doc comments are unusually
substantial — `IHighwayClient.SendAsync` explains the Send/Publish/Execute choice,
`IProcess<T>` explains competing instances and at-least-once — and none of it currently
reaches a consumer's IntelliSense. Turning this on is the cheapest documentation improvement
available in the repository, and it may surface missing-comment warnings on public members,
which is a small triage task worth doing once.

### The version property

One property drives four things: package versions, assembly informational version,
`highways --version`, and 031's zip folder name. Feature 031's T4 flagged that it does not
exist; this is where it lands, so the two features cannot disagree about what a build is
called.

---

## Per-package README

Each package ships a README that renders on nuget.org. Not a copy of the repository README —
a page for someone who arrived at that package specifically:

| Package | Opens with |
|---|---|
| `Highway.Abstractions` | Contracts only, zero dependencies. Reference it from a shared contracts library that both sides use |
| `Highway.Client` | Registration in one call, the three verbs, a link to the guide |
| `Highway.LocalServer` | **In-process, for tests and local development** — `HighwayTestServer` for integration tests, `HighwayServerBuilder` for a local run. Production deploys the distribution from Releases (D1) |

Every one links to the UserGuide, the protocol file and `constraints.md`. A developer who
arrives from nuget.org should reach the same honest documentation as one who arrives from
GitHub — including the page that lists what Highway does not guarantee.

---

## Verification: a consumer, not a `.nupkg`

A `.nupkg` that exists proves nothing. The proof is a project **outside this repository**
that restores from a local feed and compiles:

```
build/verify/
├── nuget.config          local feed pointing at the pack output
├── Verify.csproj         references Highway.Client + Highway.Server
└── Program.cs            builds a HighwayTestServer, registers AddHighway,
                          sends a message and processes it
```

Run as part of T6. It catches the failures that only appear on someone else's machine: a
missing transitive dependency, an assembly that did not get packed, a `lib/` folder for the
wrong target framework, a `PrivateAssets` that hid something needed.

A second assertion runs against `Highway.Abstractions` alone: its dependency group must be
**empty**. That property is why a contracts assembly can sit between two services without
dragging an engine into either, and it is the kind of thing that decays silently.

---

## Risks

**Version parity with the submodule.** The submodule sits at a commit whose subject is
*"Harden LightEpoch: make the epoch announce part of the slot-claim CAS (#2015)"* — a
concurrency fix — and `Version.props` there reads `2.1.2`, while the available package is
`2.1.3`. Before the switch lands, confirm that the chosen package version **contains that
commit**. Moving from source to a package that silently lacks a concurrency fix is the worst
outcome this feature could produce, and it would not show up as a compile error. If parity
cannot be established, the honest answer is to keep source references until a release that
has it, and say so in the spec rather than shipping the doubt.

**Unsafe code across the boundary.** `Highway.Server` sets `AllowUnsafeBlocks` for
`DoorbellBridge`, which pins byte arrays over Garnet types. Compiling against package
assemblies should be identical to compiling against project output, but "should be" is why
T2 runs the whole suite rather than just the build.

**Package ID availability.** `Highway.Client` is a generic identifier. If it is taken on
nuget.org, every code sample, guide and README that names a package changes. That is the open
question in `requirements.md`, and it is cheap to answer early and expensive to answer late.

---

## Testing Strategy

| Layer | What | How |
|---|---|---|
| Garnet migration | Full suite green with `PackageReference`; suite green again with `-p:UseGarnetSource=true` | Existing 948 tests, run twice |
| Clean clone | Build and test succeed **without** `--recursive` | A scripted check, once |
| Metadata | Every package has id, version, licence, README, repository URL and commit | Assertion over the packed `.nuspec` |
| Abstractions purity | Dependency group is empty | Assertion over the packed `.nuspec` |
| Consumer | A project outside the repo restores, compiles, runs a broker in-process and round-trips a message | `build/verify/` (§ above) |
| Pack hygiene | `dotnet pack` emits zero warnings | Build check |
