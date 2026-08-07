# Design: Project Skeleton

## Overview

This document defines the technical structure for the Highway solution — directory layout, project relationships, build configuration, and the Garnet dependency strategy.

## Directory Structure

```
highway/                          # repo root
├── Highway.slnx                  # Solution file (new XML format)
├── Directory.Build.props         # Shared build properties
├── Directory.Packages.props      # Central Package Management
├── .gitignore
├── .gitmodules                   # Garnet submodule declaration
├── CLAUDE.md                     # Claude Code agent instructions
├── QWEN.md                       # Qwen Code agent instructions
├── .kiro/
│   └── steering/                 # Kiro steering files
├── docs/
│   ├── product/                  # Product definition (read-only)
│   └── features/                 # Feature specs
├── libs/
│   └── garnet/                   # Git submodule → microsoft/garnet
├── src/
│   ├── Highway.Abstractions/
│   │   └── Highway.Abstractions.csproj
│   ├── Highway.Client/
│   │   └── Highway.Client.csproj
│   └── Highway.Server/
│       └── Highway.Server.csproj
└── tests/
    ├── Highway.Abstractions.Tests/
    │   └── Highway.Abstractions.Tests.csproj
    ├── Highway.Client.Tests/
    │   └── Highway.Client.Tests.csproj
    ├── Highway.Server.Tests/
    │   └── Highway.Server.Tests.csproj
    └── Highway.Integration.Tests/
        └── Highway.Integration.Tests.csproj
```

## Project Dependency Graph

```
Highway.Integration.Tests
├── Highway.Client
│   └── Highway.Abstractions
└── Highway.Server
    ├── Highway.Abstractions
    └── Garnet (libs/garnet project references)

Highway.Client.Tests → Highway.Client → Highway.Abstractions
Highway.Server.Tests → Highway.Server → Highway.Abstractions + Garnet
Highway.Abstractions.Tests → Highway.Abstractions
```

## Solution File (Highway.slnx)

Using the new `.slnx` XML format introduced in .NET 9+. Structure:

```xml
<Solution>
  <Folder Name="/src/">
    <Project Path="src/Highway.Abstractions/Highway.Abstractions.csproj" />
    <Project Path="src/Highway.Client/Highway.Client.csproj" />
    <Project Path="src/Highway.Server/Highway.Server.csproj" />
  </Folder>
  <Folder Name="/tests/">
    <Project Path="tests/Highway.Abstractions.Tests/Highway.Abstractions.Tests.csproj" />
    <Project Path="tests/Highway.Client.Tests/Highway.Client.Tests.csproj" />
    <Project Path="tests/Highway.Server.Tests/Highway.Server.Tests.csproj" />
    <Project Path="tests/Highway.Integration.Tests/Highway.Integration.Tests.csproj" />
  </Folder>
</Solution>
```

## Shared Build Configuration

### Directory.Build.props

Applied to ALL projects in the tree:

```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>latest</LangVersion>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
  </PropertyGroup>
</Project>
```

### Directory.Packages.props

Central Package Management — all versions pinned in one file:

```xml
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
  </PropertyGroup>
  <ItemGroup>
    <!-- Client dependencies -->
    <PackageVersion Include="StackExchange.Redis" Version="2.8.x" />
    <PackageVersion Include="Microsoft.Extensions.DependencyInjection.Abstractions" Version="10.0.0" />
    <PackageVersion Include="Microsoft.Extensions.Hosting.Abstractions" Version="10.0.0" />
    <PackageVersion Include="Microsoft.Extensions.Logging.Abstractions" Version="10.0.0" />

    <!-- Test dependencies -->
    <PackageVersion Include="Microsoft.NET.Test.Sdk" Version="17.x" />
    <PackageVersion Include="xunit" Version="2.9.x" />
    <PackageVersion Include="xunit.runner.visualstudio" Version="2.9.x" />
    <PackageVersion Include="FluentAssertions" Version="7.x" />
    <PackageVersion Include="NSubstitute" Version="5.x" />
  </ItemGroup>
</Project>
```

Note: Exact versions to be pinned at implementation time based on latest stable releases.

## Garnet Integration: Git Submodule

### Decision: Submodule over NuGet

**Why submodule:**

The product definition states Highway.Server registers custom `HW.*` commands using Garnet's C# extensibility. While the research doc recommends "build entirely out of standard RESP commands" for v1, the product doc's final architecture positions Highway.Server as a Garnet extension. To register custom commands, we need access to Garnet's internal types (`CustomRawStringCommand`, `CustomObjectCommand`, `IGarnetApi`, etc.) which are exposed via project reference but may not be fully available via the NuGet package's public surface.

Additionally:
- Source-level debugging of Garnet during development
- Ability to patch Garnet if needed (fork from submodule)
- Build the exact Garnet assembly we need without pulling unused components
- No version lag — we pin to a specific commit and control upgrades

**Why not NuGet:**
- The `Microsoft.Garnet` NuGet package exposes the server as a pre-built binary. Custom command registration requires referencing internal Garnet projects that may not be in the public NuGet.
- We lose source-level debugging.
- We can't patch bugs without waiting for a release.

### Submodule Configuration

```gitmodules
[submodule "libs/garnet"]
    path = libs/garnet
    url = https://github.com/microsoft/garnet
    branch = main
```

Pin to a specific tag (e.g., `v1.0.35` stable or `v2.0.0-beta.5`) at implementation time.

### Which Garnet projects to reference

From the Garnet repository structure, Highway.Server needs:
- `libs/garnet/libs/server/Garnet.server.csproj` — The embeddable server with custom command registration
- Possibly `libs/garnet/libs/common/Garnet.common.csproj` — Shared types

The exact project references will be determined during implementation by inspecting the Garnet repo structure at the pinned commit.

## Individual Project Configurations

### Highway.Abstractions.csproj

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <!-- No PackageReferences. Zero dependencies. -->
  <!-- TargetFramework, Nullable, ImplicitUsings inherited from Directory.Build.props -->
</Project>
```

### Highway.Client.csproj

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <ProjectReference Include="..\Highway.Abstractions\Highway.Abstractions.csproj" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="StackExchange.Redis" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" />
    <PackageReference Include="Microsoft.Extensions.Hosting.Abstractions" />
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" />
  </ItemGroup>
</Project>
```

### Highway.Server.csproj

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <ProjectReference Include="..\Highway.Abstractions\Highway.Abstractions.csproj" />
    <ProjectReference Include="..\..\libs\garnet\libs\server\Garnet.server.csproj" />
  </ItemGroup>
</Project>
```

### Test Project Template (all four follow this pattern)

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\Highway.{Name}\Highway.{Name}.csproj" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="xunit" />
    <PackageReference Include="xunit.runner.visualstudio" />
    <PackageReference Include="FluentAssertions" />
    <PackageReference Include="NSubstitute" />
  </ItemGroup>
</Project>
```

## .gitignore

Standard .NET gitignore covering:
- `bin/`, `obj/` — build output
- `.vs/`, `*.user`, `*.suo` — Visual Studio IDE files
- `TestResults/` — test output
- `*.nupkg` — NuGet package output
- `BenchmarkDotNet.Artifacts/` — benchmark output

## Build Verification

After setup, these commands must succeed:

```bash
git submodule update --init --recursive
dotnet restore Highway.slnx
dotnet build Highway.slnx
dotnet test Highway.slnx
```
