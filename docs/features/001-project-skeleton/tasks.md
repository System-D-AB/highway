# Tasks: Project Skeleton

## Task Dependency Graph

```
T1 (.gitignore)
T2 (Directory.Build.props) 
T3 (Directory.Packages.props) → depends on T2
T4 (Highway.Abstractions) → depends on T2, T3
T5 (Garnet submodule) 
T6 (Highway.Client) → depends on T4
T7 (Highway.Server) → depends on T4, T5
T8 (Test projects) → depends on T4, T6, T7
T9 (Solution file) → depends on T4, T6, T7, T8
T10 (Build verification) → depends on all above
```

## Tasks

- [x] ### Task 1: Create .gitignore

**Fulfills:** Requirement 8

**Steps:**
1. Create `.gitignore` at repo root with standard .NET rules

**Done criteria:**
- `.gitignore` exists with rules for `bin/`, `obj/`, `.vs/`, `*.user`, `*.suo`, `TestResults/`, `*.nupkg`, `BenchmarkDotNet.Artifacts/`

---

- [x] ### Task 2: Create Directory.Build.props

**Fulfills:** Requirement 6

**Steps:**
1. Create `Directory.Build.props` at repo root
2. Set shared properties: `TargetFramework=net10.0`, `Nullable=enable`, `ImplicitUsings=enable`, `LangVersion=latest`

**Done criteria:**
- File exists at repo root
- Contains the four shared properties in a `<PropertyGroup>`

---

- [x] ### Task 3: Create Directory.Packages.props

**Fulfills:** Requirement 6

**Steps:**
1. Create `Directory.Packages.props` at repo root
2. Enable `ManagePackageVersionsCentrally`
3. Add `<PackageVersion>` entries for all dependencies:
   - StackExchange.Redis
   - Microsoft.Extensions.DependencyInjection.Abstractions
   - Microsoft.Extensions.Hosting.Abstractions
   - Microsoft.Extensions.Logging.Abstractions
   - Microsoft.NET.Test.Sdk
   - xunit
   - xunit.runner.visualstudio
   - FluentAssertions
   - NSubstitute

**Done criteria:**
- File exists at repo root
- All package versions are pinned to specific stable versions
- `ManagePackageVersionsCentrally` is `true`

---

- [x] ### Task 4: Create Highway.Abstractions Project

**Fulfills:** Requirement 2

**Steps:**
1. Create directory `src/Highway.Abstractions/`
2. Create `Highway.Abstractions.csproj` with no package references
3. Create a placeholder `_Placeholder.cs` file (empty namespace declaration) so the project compiles
4. Verify `dotnet build src/Highway.Abstractions/`

**Done criteria:**
- Project compiles with zero dependencies
- No `TargetFramework` specified in .csproj (inherited)
- Root namespace is `Highway.Abstractions`

---

- [x] ### Task 5: Add Garnet as Git Submodule

**Fulfills:** Requirement 7

**Steps:**
1. Run `git submodule add https://github.com/microsoft/garnet libs/garnet`
2. Pin to a specific stable tag/commit
3. Run `git submodule update --init --recursive`
4. Verify the Garnet source is present at `libs/garnet/`
5. Identify the correct Garnet project(s) to reference (inspect `libs/garnet/` structure)

**Done criteria:**
- `.gitmodules` file exists declaring submodule at `libs/garnet`
- Garnet source is checked out at the pinned commit
- The Garnet server project (e.g., `libs/garnet/libs/server/Garnet.server.csproj`) is accessible

---

- [x] ### Task 6: Create Highway.Client Project

**Fulfills:** Requirement 3

**Steps:**
1. Create directory `src/Highway.Client/`
2. Create `Highway.Client.csproj` with:
   - Project reference to `Highway.Abstractions`
   - Package references (version-less, from central management): StackExchange.Redis, Microsoft.Extensions.DependencyInjection.Abstractions, Microsoft.Extensions.Hosting.Abstractions, Microsoft.Extensions.Logging.Abstractions
3. Create a placeholder file so the project compiles
4. Verify `dotnet build src/Highway.Client/`

**Done criteria:**
- Project compiles successfully
- Has project reference to Highway.Abstractions
- Has four NuGet package references (no version attributes in .csproj)

---

- [x] ### Task 7: Create Highway.Server Project

**Fulfills:** Requirement 4

**Steps:**
1. Create directory `src/Highway.Server/`
2. Create `Highway.Server.csproj` with:
   - Project reference to `Highway.Abstractions`
   - Project reference to appropriate Garnet project(s) from `libs/garnet/`
3. Create a placeholder file so the project compiles
4. Verify `dotnet build src/Highway.Server/`

**Done criteria:**
- Project compiles successfully
- Has project reference to Highway.Abstractions
- Has project reference to Garnet from the submodule
- Root namespace is `Highway.Server`

---

- [x] ### Task 8: Create Test Projects

**Fulfills:** Requirement 5

**Steps:**
1. Create `tests/Highway.Abstractions.Tests/` with .csproj referencing Highway.Abstractions + test packages
2. Create `tests/Highway.Client.Tests/` with .csproj referencing Highway.Client + test packages
3. Create `tests/Highway.Server.Tests/` with .csproj referencing Highway.Server + test packages
4. Create `tests/Highway.Integration.Tests/` with .csproj referencing Highway.Client + Highway.Server + test packages
5. Each test project gets a placeholder test class with one `[Fact]` that passes
6. Set `<IsPackable>false</IsPackable>` and `<IsTestProject>true</IsTestProject>` on all test projects
7. Verify `dotnet test` runs on each

**Done criteria:**
- All four test projects compile
- Each has at least one passing test
- `dotnet test` discovers and runs all tests
- No version attributes on package references (central management)

---

- [x] ### Task 9: Create Highway.slnx Solution File

**Fulfills:** Requirement 1

**Steps:**
1. Create `Highway.slnx` at repo root
2. Add all source projects under a `/src/` solution folder
3. Add all test projects under a `/tests/` solution folder
4. Verify `dotnet build Highway.slnx` compiles everything

**Done criteria:**
- Solution file exists in `.slnx` XML format
- Contains all 7 projects organized in solution folders
- `dotnet build Highway.slnx` succeeds
- `dotnet test Highway.slnx` discovers and runs all tests

---

- [x] ### Task 10: Full Build Verification

**Fulfills:** All requirements

**Steps:**
1. From a clean clone, run:
   ```
   git submodule update --init --recursive
   dotnet restore Highway.slnx
   dotnet build Highway.slnx
   dotnet test Highway.slnx
   ```
2. Verify zero errors, zero warnings (or document expected warnings)
3. Verify no build artifacts are tracked by git

**Done criteria:**
- All four commands pass cleanly
- `git status` shows no untracked build output
- Solution builds on a fresh machine after submodule init
