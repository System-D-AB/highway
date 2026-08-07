# Feature: Project Skeleton

## Introduction

Establish the foundational .NET solution structure for Highway — three class library projects, a solution file, shared build configuration, and the Garnet dependency strategy. This skeleton is the base that all subsequent features build on.

## Glossary

- **slnx** — The new XML-based .NET solution file format (replaces .sln)
- **Directory.Build.props** — MSBuild file that applies shared properties to all projects in a directory tree
- **Directory.Packages.props** — Central Package Management file that pins all NuGet versions in one place
- **Garnet** — Microsoft Research cache-store (MIT-licensed, C#, RESP protocol) used as Highway's broker substrate
- **git submodule** — A git mechanism to embed one repository inside another at a pinned commit

## Requirements

### Requirement 1: Solution File

**User Story:** As a developer, I want a single solution file that contains all Highway projects, so that I can build and navigate the entire codebase from one entry point.

#### Acceptance Criteria

1. A `.slnx` solution file exists at the repository root named `Highway.slnx`
2. The solution contains three source projects: `Highway.Abstractions`, `Highway.Client`, `Highway.Server`
3. The solution contains four test projects: `Highway.Abstractions.Tests`, `Highway.Client.Tests`, `Highway.Server.Tests`, `Highway.Integration.Tests`
4. Running `dotnet build Highway.slnx` from the repo root compiles all projects without errors
5. Projects are organized in solution folders: `src/` for source projects, `tests/` for test projects

### Requirement 2: Highway.Abstractions Project

**User Story:** As a library consumer, I want a zero-dependency contracts package containing all shared interfaces, attributes, and base classes, so that my shared contract assemblies have no transitive dependencies.

#### Acceptance Criteria

1. Project exists at `src/Highway.Abstractions/Highway.Abstractions.csproj`
2. Targets `net10.0` with nullable reference types enabled and implicit usings enabled
3. Contains no package references (zero external dependencies)
4. Root namespace is `Highway.Abstractions`
5. Project compiles independently with `dotnet build`

### Requirement 3: Highway.Client Project

**User Story:** As an application developer, I want a client library that provides the engine, assembly scanning, DI integration, and serialization, so that I can host services and call remote services from my application.

#### Acceptance Criteria

1. Project exists at `src/Highway.Client/Highway.Client.csproj`
2. Targets `net10.0` with nullable reference types enabled and implicit usings enabled
3. References `Highway.Abstractions` as a project reference
4. References `StackExchange.Redis` for RESP communication
5. References `Microsoft.Extensions.DependencyInjection.Abstractions` and `Microsoft.Extensions.Hosting.Abstractions` for DI/hosting integration
6. References `System.Text.Json` (or relies on the framework-included version) for serialization
7. Root namespace is `Highway.Client`
8. Project compiles independently with `dotnet build`

### Requirement 4: Highway.Server Project

**User Story:** As an operator, I want a server package that extends Garnet with Highway's custom commands, so that I can run the Highway broker as a standalone process or embed it for testing.

#### Acceptance Criteria

1. Project exists at `src/Highway.Server/Highway.Server.csproj`
2. Targets `net10.0` with nullable reference types enabled and implicit usings enabled
3. References `Highway.Abstractions` as a project reference
4. Garnet is included as a **git submodule** at `libs/garnet/` pointing to `https://github.com/microsoft/garnet`
5. References the Garnet server project(s) from the submodule via project reference (not NuGet), enabling source-level access to register custom commands
6. Root namespace is `Highway.Server`
7. Project compiles independently with `dotnet build`

### Requirement 5: Test Projects

**User Story:** As a developer, I want test projects for each source project plus an integration test project, so that I can validate behavior at unit and integration levels.

#### Acceptance Criteria

1. `tests/Highway.Abstractions.Tests/Highway.Abstractions.Tests.csproj` exists and references `Highway.Abstractions`
2. `tests/Highway.Client.Tests/Highway.Client.Tests.csproj` exists and references `Highway.Client`
3. `tests/Highway.Server.Tests/Highway.Server.Tests.csproj` exists and references `Highway.Server`
4. `tests/Highway.Integration.Tests/Highway.Integration.Tests.csproj` exists and references both `Highway.Client` and `Highway.Server`
5. All test projects target `net10.0`
6. All test projects reference xUnit, FluentAssertions, and NSubstitute
7. Running `dotnet test Highway.slnx` discovers and runs tests from all test projects

### Requirement 6: Shared Build Configuration

**User Story:** As a developer, I want common build settings applied to all projects automatically, so that I don't have to repeat framework version, nullable settings, and other properties in every .csproj file.

#### Acceptance Criteria

1. A `Directory.Build.props` file at the repo root sets: `TargetFramework=net10.0`, `Nullable=enable`, `ImplicitUsings=enable`, `LangVersion=latest`
2. A `Directory.Packages.props` file at the repo root enables Central Package Management and pins all NuGet dependency versions
3. Individual `.csproj` files do not specify `TargetFramework`, `Nullable`, or `ImplicitUsings` (inherited from Directory.Build.props)
4. Individual `.csproj` files do not specify package versions (inherited from Directory.Packages.props)

### Requirement 7: Garnet Integration Strategy

**User Story:** As a developer, I want Garnet included as a git submodule with source-level project references, so that Highway.Server can register custom RESP commands using Garnet's internal extensibility APIs.

#### Acceptance Criteria

1. A `.gitmodules` file at the repo root declares a submodule at path `libs/garnet` pointing to `https://github.com/microsoft/garnet`
2. The submodule is pinned to a specific stable tag or commit (v1.0.x stable release or latest v2 beta)
3. `Highway.Server.csproj` references the appropriate Garnet project(s) from `libs/garnet/` as `<ProjectReference>`
4. The solution builds successfully after `git submodule update --init`
5. A `README.md` or comment in the build files documents why submodule was chosen over NuGet (source access for custom command registration)

### Requirement 8: .gitignore and Repository Hygiene

**User Story:** As a developer, I want proper git ignore rules, so that build artifacts, IDE files, and binary outputs are not committed.

#### Acceptance Criteria

1. A `.gitignore` file exists at the repo root with rules for: `bin/`, `obj/`, `.vs/`, `*.user`, `*.suo`, `TestResults/`, NuGet packages
2. No build output directories are tracked in git
