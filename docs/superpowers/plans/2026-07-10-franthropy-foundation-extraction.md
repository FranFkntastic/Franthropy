# Franthropy Foundation Extraction Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Convert the standalone `Franthropy.Dalamud` seed into the canonical `Franthropy` shared toolkit repo and migrate ComplicatedMarketBoard away from its embedded Franthropy seed.

**Architecture:** The foundation phase keeps only the existing world/travel helper scope in Franthropy. The standalone repo becomes a multi-project-ready layout with `src/Franthropy.Dalamud` and `tests/Franthropy.Dalamud.Tests`; CMB consumes the standalone project through an overridable MSBuild property that defaults to a sibling checkout. CMB-specific market browsing and MarketMafioso route/acquisition behavior remain untouched.

**Tech Stack:** C#/.NET 10, `Microsoft.NET.Sdk`, `Dalamud.NET.Sdk` consumers, xUnit, PowerShell, Git submodule cleanup.

---

## Source Specs

- `F:\Everything (HDD)\Misc\Gooseworks (Projects)\FFXIV-Development\Franthropy.Dalamud\docs\superpowers\specs\2026-07-10-franthropy-foundation-design.md`
- `F:\Everything (HDD)\Misc\Gooseworks (Projects)\FFXIV-Development\ComplicatedMarketBoard\AGENTS.md`

## Repositories

- Franthropy current checkout: `F:\Everything (HDD)\Misc\Gooseworks (Projects)\FFXIV-Development\Franthropy.Dalamud`
- Franthropy target checkout: `F:\Everything (HDD)\Misc\Gooseworks (Projects)\FFXIV-Development\Franthropy`
- ComplicatedMarketBoard checkout: `F:\Everything (HDD)\Misc\Gooseworks (Projects)\FFXIV-Development\ComplicatedMarketBoard`

## Planned File Structure

Franthropy repo:

- Move: `Franthropy.Dalamud.csproj` -> `src/Franthropy.Dalamud/Franthropy.Dalamud.csproj`
- Move: `Travel/*.cs` -> `src/Franthropy.Dalamud/Travel/*.cs`
- Move: `Worlds/*.cs` -> `src/Franthropy.Dalamud/Worlds/*.cs`
- Create: `Franthropy.sln`
- Create: `tests/Franthropy.Dalamud.Tests/Franthropy.Dalamud.Tests.csproj`
- Create: `tests/Franthropy.Dalamud.Tests/Worlds/WorldCatalogTests.cs`
- Create: `tests/Franthropy.Dalamud.Tests/Travel/LifestreamTravelCommandBuilderTests.cs`
- Modify: `README.md`
- Modify: `Franthropy.Dalamud.csproj` after move

ComplicatedMarketBoard repo:

- Modify: `.gitmodules`
- Modify: `ComplicatedMarketBoard.sln`
- Modify: `ComplicatedMarketBoard/ComplicatedMarketBoard.csproj`
- Remove: `Franthropy.Dalamud/`
- Inspect/remove if safe: `.git/modules/Franthropy.Dalamud`
- Verify unchanged behavior: `tools/Build-DalamudRelease.ps1` should still package `Franthropy.Dalamud.dll` and `Franthropy.Dalamud.xml`

---

### Task 1: Preflight And Dirty-State Audit

**Files:**
- Inspect only: `F:\Everything (HDD)\Misc\Gooseworks (Projects)\FFXIV-Development\Franthropy.Dalamud`
- Inspect only: `F:\Everything (HDD)\Misc\Gooseworks (Projects)\FFXIV-Development\ComplicatedMarketBoard`

- [ ] **Step 1: Check Franthropy status**

Run:

```powershell
git -C "F:\Everything (HDD)\Misc\Gooseworks (Projects)\FFXIV-Development\Franthropy.Dalamud" status -sb
git -C "F:\Everything (HDD)\Misc\Gooseworks (Projects)\FFXIV-Development\Franthropy.Dalamud" log -3 --oneline
```

Expected: Franthropy is clean except for any active implementation-plan branch/commit already intentionally created. If dirty files appear, stop and identify them before editing.

- [ ] **Step 2: Check CMB status and preserve existing work**

Run:

```powershell
git -C "F:\Everything (HDD)\Misc\Gooseworks (Projects)\FFXIV-Development\ComplicatedMarketBoard" status -sb
git -C "F:\Everything (HDD)\Misc\Gooseworks (Projects)\FFXIV-Development\ComplicatedMarketBoard" status --short
```

Expected: CMB may already be dirty on `local-dev`. Do not revert existing CMB changes. Record which files are dirty before starting the Franthropy migration.

- [ ] **Step 3: Inspect the CMB Franthropy nested repo/submodule state**

Run:

```powershell
git -C "F:\Everything (HDD)\Misc\Gooseworks (Projects)\FFXIV-Development\ComplicatedMarketBoard" submodule status
git -C "F:\Everything (HDD)\Misc\Gooseworks (Projects)\FFXIV-Development\ComplicatedMarketBoard" ls-files -s Franthropy.Dalamud
git -C "F:\Everything (HDD)\Misc\Gooseworks (Projects)\FFXIV-Development\ComplicatedMarketBoard\Franthropy.Dalamud" status -sb
git -C "F:\Everything (HDD)\Misc\Gooseworks (Projects)\FFXIV-Development\ComplicatedMarketBoard\Franthropy.Dalamud" log -1 --oneline
git -C "F:\Everything (HDD)\Misc\Gooseworks (Projects)\FFXIV-Development\Franthropy.Dalamud" log -1 --oneline
```

Expected: You know whether CMB tracks a gitlink, whether the nested repo is clean, and whether nested Franthropy HEAD matches standalone Franthropy HEAD.

- [ ] **Step 4: Decide whether CMB can be edited in this pass**

If CMB has unrelated dirty changes in the same files this plan needs (`.gitmodules`, `ComplicatedMarketBoard.sln`, `ComplicatedMarketBoard/ComplicatedMarketBoard.csproj`, `tools/Build-DalamudRelease.ps1`), inspect those diffs first:

```powershell
git -C "F:\Everything (HDD)\Misc\Gooseworks (Projects)\FFXIV-Development\ComplicatedMarketBoard" diff -- .gitmodules ComplicatedMarketBoard.sln ComplicatedMarketBoard\ComplicatedMarketBoard.csproj tools\Build-DalamudRelease.ps1
```

Expected: If those existing changes are unrelated and conflict with this migration, pause for user direction. If they are part of the same intended CMB/Franthropy work, continue and preserve them.

---

### Task 2: Restructure The Standalone Franthropy Repo

**Files:**
- Move: `F:\Everything (HDD)\Misc\Gooseworks (Projects)\FFXIV-Development\Franthropy.Dalamud\Franthropy.Dalamud.csproj`
- Move: `F:\Everything (HDD)\Misc\Gooseworks (Projects)\FFXIV-Development\Franthropy.Dalamud\Travel\*`
- Move: `F:\Everything (HDD)\Misc\Gooseworks (Projects)\FFXIV-Development\Franthropy.Dalamud\Worlds\*`
- Create: `F:\Everything (HDD)\Misc\Gooseworks (Projects)\FFXIV-Development\Franthropy.Dalamud\src\Franthropy.Dalamud\`

- [ ] **Step 1: Create target source folders**

Run:

```powershell
$repo = "F:\Everything (HDD)\Misc\Gooseworks (Projects)\FFXIV-Development\Franthropy.Dalamud"
New-Item -ItemType Directory -Force -Path "$repo\src\Franthropy.Dalamud\Travel", "$repo\src\Franthropy.Dalamud\Worlds"
```

Expected: `src/Franthropy.Dalamud/Travel` and `src/Franthropy.Dalamud/Worlds` exist.

- [ ] **Step 2: Move project and source files with Git**

Run:

```powershell
$repo = "F:\Everything (HDD)\Misc\Gooseworks (Projects)\FFXIV-Development\Franthropy.Dalamud"
git -C $repo mv Franthropy.Dalamud.csproj src/Franthropy.Dalamud/Franthropy.Dalamud.csproj
git -C $repo mv Travel/WorldTravelRequest.cs src/Franthropy.Dalamud/Travel/WorldTravelRequest.cs
git -C $repo mv Travel/WorldTravelCommandResult.cs src/Franthropy.Dalamud/Travel/WorldTravelCommandResult.cs
git -C $repo mv Travel/LifestreamTravelCommandBuilder.cs src/Franthropy.Dalamud/Travel/LifestreamTravelCommandBuilder.cs
git -C $repo mv Worlds/WorldCatalog.cs src/Franthropy.Dalamud/Worlds/WorldCatalog.cs
```

Expected: `git status --short` shows renames into `src/Franthropy.Dalamud`.

- [ ] **Step 3: Remove empty legacy folders**

Run:

```powershell
$repo = "F:\Everything (HDD)\Misc\Gooseworks (Projects)\FFXIV-Development\Franthropy.Dalamud"
Remove-Item -LiteralPath "$repo\Travel" -Force
Remove-Item -LiteralPath "$repo\Worlds" -Force
```

Expected: root-level `Travel` and `Worlds` folders are gone.

- [ ] **Step 4: Build the moved project directly**

Run:

```powershell
dotnet build "F:\Everything (HDD)\Misc\Gooseworks (Projects)\FFXIV-Development\Franthropy.Dalamud\src\Franthropy.Dalamud\Franthropy.Dalamud.csproj" -c Debug
```

Expected: Build succeeds with no compile errors.

---

### Task 3: Add Franthropy Solution And Tests

**Files:**
- Create: `F:\Everything (HDD)\Misc\Gooseworks (Projects)\FFXIV-Development\Franthropy.Dalamud\Franthropy.sln`
- Create: `F:\Everything (HDD)\Misc\Gooseworks (Projects)\FFXIV-Development\Franthropy.Dalamud\tests\Franthropy.Dalamud.Tests\Franthropy.Dalamud.Tests.csproj`
- Create: `F:\Everything (HDD)\Misc\Gooseworks (Projects)\FFXIV-Development\Franthropy.Dalamud\tests\Franthropy.Dalamud.Tests\Worlds\WorldCatalogTests.cs`
- Create: `F:\Everything (HDD)\Misc\Gooseworks (Projects)\FFXIV-Development\Franthropy.Dalamud\tests\Franthropy.Dalamud.Tests\Travel\LifestreamTravelCommandBuilderTests.cs`

- [ ] **Step 1: Create solution and test project skeleton**

Run:

```powershell
$repo = "F:\Everything (HDD)\Misc\Gooseworks (Projects)\FFXIV-Development\Franthropy.Dalamud"
dotnet new sln --name Franthropy --output $repo
dotnet new xunit --name Franthropy.Dalamud.Tests --output "$repo\tests\Franthropy.Dalamud.Tests" --framework net10.0-windows
dotnet sln "$repo\Franthropy.sln" add "$repo\src\Franthropy.Dalamud\Franthropy.Dalamud.csproj"
dotnet sln "$repo\Franthropy.sln" add "$repo\tests\Franthropy.Dalamud.Tests\Franthropy.Dalamud.Tests.csproj"
dotnet add "$repo\tests\Franthropy.Dalamud.Tests\Franthropy.Dalamud.Tests.csproj" reference "$repo\src\Franthropy.Dalamud\Franthropy.Dalamud.csproj"
```

Expected: `Franthropy.sln` and `tests/Franthropy.Dalamud.Tests/Franthropy.Dalamud.Tests.csproj` exist and include the project reference.

- [ ] **Step 2: Replace the generated test project file with explicit package versions**

Edit `tests/Franthropy.Dalamud.Tests/Franthropy.Dalamud.Tests.csproj` to:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0-windows</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
    <Platforms>x64;AnyCPU</Platforms>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="coverlet.collector" Version="6.0.4" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
    <PackageReference Include="xunit" Version="2.9.3" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
  </ItemGroup>

  <ItemGroup>
    <Using Include="Xunit" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\Franthropy.Dalamud\Franthropy.Dalamud.csproj" />
  </ItemGroup>
</Project>
```

Expected: Test project uses the same xUnit package family used by nearby repos.

- [ ] **Step 3: Remove generated sample test**

Run:

```powershell
$repo = "F:\Everything (HDD)\Misc\Gooseworks (Projects)\FFXIV-Development\Franthropy.Dalamud"
Remove-Item -LiteralPath "$repo\tests\Franthropy.Dalamud.Tests\UnitTest1.cs" -Force
New-Item -ItemType Directory -Force -Path "$repo\tests\Franthropy.Dalamud.Tests\Worlds", "$repo\tests\Franthropy.Dalamud.Tests\Travel"
```

Expected: `UnitTest1.cs` is gone; `Worlds` and `Travel` test folders exist.

- [ ] **Step 4: Write `WorldCatalog` tests**

Create `tests/Franthropy.Dalamud.Tests/Worlds/WorldCatalogTests.cs`:

```csharp
using Franthropy.Dalamud.Worlds;

namespace Franthropy.Dalamud.Tests.Worlds;

public sealed class WorldCatalogTests
{
    [Fact]
    public void TryGetWorld_MatchesTrimmedNameCaseInsensitively()
    {
        var catalog = new WorldCatalog(
        [
            new WorldInfo("Siren", "Aether", "North America", 57),
        ]);

        var found = catalog.TryGetWorld("  siren  ", out var world);

        Assert.True(found);
        Assert.Equal("Siren", world.Name);
        Assert.Equal("Aether", world.DataCenter);
        Assert.Equal("North America", world.Region);
        Assert.Equal(57u, world.RowId);
    }

    [Fact]
    public void TryGetWorld_ReturnsFalseForBlankOrUnknownWorld()
    {
        var catalog = new WorldCatalog(
        [
            new WorldInfo("Siren", "Aether", "North America", 57),
        ]);

        Assert.False(catalog.TryGetWorld("", out _));
        Assert.False(catalog.TryGetWorld("NotAWorld", out _));
    }

    [Fact]
    public void Constructor_UsesFirstWorldWhenDuplicateNamesAreProvided()
    {
        var catalog = new WorldCatalog(
        [
            new WorldInfo("Siren", "Aether", "North America", 57),
            new WorldInfo("siren", "Other", "Other Region", 999),
        ]);

        var found = catalog.TryGetWorld("Siren", out var world);

        Assert.True(found);
        Assert.Equal("Aether", world.DataCenter);
        Assert.Equal(57u, world.RowId);
    }
}
```

- [ ] **Step 5: Write `LifestreamTravelCommandBuilder` tests**

Create `tests/Franthropy.Dalamud.Tests/Travel/LifestreamTravelCommandBuilderTests.cs`:

```csharp
using Franthropy.Dalamud.Travel;
using Franthropy.Dalamud.Worlds;

namespace Franthropy.Dalamud.Tests.Travel;

public sealed class LifestreamTravelCommandBuilderTests
{
    [Fact]
    public void TryBuildMarketBoardTravel_UsesNearestMarketBoardCommandForCurrentWorld()
    {
        var builder = CreateBuilder();

        var result = builder.TryBuildMarketBoardTravel("Siren", "siren", out var request);

        Assert.True(result.Success);
        Assert.Equal("/li mb", result.Command);
        Assert.NotNull(request);
        Assert.Equal("Siren", request.TargetWorld);
        Assert.Equal("siren", request.CurrentWorld);
        Assert.Equal("/li mb", request.Command);
        Assert.True(request.IsCurrentWorld);
    }

    [Fact]
    public void TryBuildMarketBoardTravel_UsesWorldMarketBoardCommandForDifferentWorld()
    {
        var builder = CreateBuilder();

        var result = builder.TryBuildMarketBoardTravel("Siren", "Gilgamesh", out var request);

        Assert.True(result.Success);
        Assert.Equal("/li Siren mb", result.Command);
        Assert.NotNull(request);
        Assert.Equal("Siren", request.TargetWorld);
        Assert.Equal("Gilgamesh", request.CurrentWorld);
        Assert.Equal("/li Siren mb", request.Command);
        Assert.False(request.IsCurrentWorld);
    }

    [Theory]
    [InlineData("", "Siren", "Target world is required.")]
    [InlineData("Unknown", "Siren", "Unknown world: Unknown.")]
    [InlineData("Siren", "", "Current world is unavailable.")]
    public void TryBuildMarketBoardTravel_RejectsInvalidInputs(
        string targetWorld,
        string currentWorld,
        string expectedMessage)
    {
        var builder = CreateBuilder();

        var result = builder.TryBuildMarketBoardTravel(targetWorld, currentWorld, out var request);

        Assert.False(result.Success);
        Assert.Null(result.Command);
        Assert.Null(request);
        Assert.Equal(expectedMessage, result.Message);
    }

    private static LifestreamTravelCommandBuilder CreateBuilder() =>
        new(new WorldCatalog(
        [
            new WorldInfo("Siren", "Aether", "North America", 57),
            new WorldInfo("Gilgamesh", "Aether", "North America", 63),
        ]));
}
```

- [ ] **Step 6: Run tests**

Run:

```powershell
dotnet test "F:\Everything (HDD)\Misc\Gooseworks (Projects)\FFXIV-Development\Franthropy.Dalamud\Franthropy.sln" -c Debug
```

Expected: All Franthropy tests pass.

- [ ] **Step 7: Commit Franthropy restructure and tests**

Run:

```powershell
git -C "F:\Everything (HDD)\Misc\Gooseworks (Projects)\FFXIV-Development\Franthropy.Dalamud" status --short
git -C "F:\Everything (HDD)\Misc\Gooseworks (Projects)\FFXIV-Development\Franthropy.Dalamud" add src tests Franthropy.sln
git -C "F:\Everything (HDD)\Misc\Gooseworks (Projects)\FFXIV-Development\Franthropy.Dalamud" commit -m "chore: restructure franthropy foundation"
```

Expected: Commit succeeds in the Franthropy repo.

---

### Task 4: Update Franthropy Identity Documentation

**Files:**
- Modify: `F:\Everything (HDD)\Misc\Gooseworks (Projects)\FFXIV-Development\Franthropy.Dalamud\README.md`
- Modify: `F:\Everything (HDD)\Misc\Gooseworks (Projects)\FFXIV-Development\Franthropy.Dalamud\src\Franthropy.Dalamud\Franthropy.Dalamud.csproj`

- [ ] **Step 1: Update project metadata if the GitHub repo has been renamed**

If the GitHub repo is renamed to `Franthropy`, change `PackageProjectUrl` in `src/Franthropy.Dalamud/Franthropy.Dalamud.csproj` to:

```xml
<PackageProjectUrl>https://github.com/FranFkntastic/Franthropy</PackageProjectUrl>
```

If the GitHub repo is not renamed yet, keep the existing URL and add a README note in Step 2 that only the local solution identity changed.

- [ ] **Step 2: Update README with repo intent and layout**

Replace `README.md` with:

```markdown
# Franthropy

Shared FFXIV toolkit libraries for Franthropy plugins and tools.

## Projects

- `src/Franthropy.Dalamud` - Dalamud-aware helper primitives such as world catalog lookups and Lifestream market-board travel command construction.

## Current Scope

The first foundation scope is intentionally small:

- world catalog lookup
- Lifestream market-board travel command construction

Market-board read models, purchase automation, Universalis helpers, and Craft Architect integration are future phases.

## Consuming Locally

Consumer repositories should reference the specific project they need. During local development, sibling checkouts are expected:

```text
FFXIV-Development/
  ComplicatedMarketBoard/
  Franthropy/
```

ComplicatedMarketBoard can then reference:

```text
..\Franthropy\src\Franthropy.Dalamud\Franthropy.Dalamud.csproj
```

## Build

```powershell
dotnet test .\Franthropy.sln -c Debug
```
```

- [ ] **Step 3: Run Franthropy tests**

Run:

```powershell
dotnet test "F:\Everything (HDD)\Misc\Gooseworks (Projects)\FFXIV-Development\Franthropy.Dalamud\Franthropy.sln" -c Debug
git -C "F:\Everything (HDD)\Misc\Gooseworks (Projects)\FFXIV-Development\Franthropy.Dalamud" diff --check
```

Expected: Tests pass and diff check reports no whitespace errors.

- [ ] **Step 4: Commit Franthropy docs/metadata**

Run:

```powershell
git -C "F:\Everything (HDD)\Misc\Gooseworks (Projects)\FFXIV-Development\Franthropy.Dalamud" add README.md src/Franthropy.Dalamud/Franthropy.Dalamud.csproj
git -C "F:\Everything (HDD)\Misc\Gooseworks (Projects)\FFXIV-Development\Franthropy.Dalamud" commit -m "docs: describe franthropy repository layout"
```

Expected: Commit succeeds if README or metadata changed. If no files changed because the repo URL was intentionally left as-is and README already matches, skip this commit and record that no docs/metadata commit was needed.

---

### Task 5: Rename Or Reclone The Local Franthropy Checkout

**Files:**
- Filesystem path change: `F:\Everything (HDD)\Misc\Gooseworks (Projects)\FFXIV-Development\Franthropy.Dalamud` -> `F:\Everything (HDD)\Misc\Gooseworks (Projects)\FFXIV-Development\Franthropy`

- [ ] **Step 1: Verify Franthropy worktree is clean before folder rename**

Run:

```powershell
git -C "F:\Everything (HDD)\Misc\Gooseworks (Projects)\FFXIV-Development\Franthropy.Dalamud" status --short
```

Expected: No output. If there are changes, commit or intentionally stop before renaming.

- [ ] **Step 2: Rename the local checkout folder**

Run from `F:\Everything (HDD)\Misc\Gooseworks (Projects)\FFXIV-Development`:

```powershell
$parent = "F:\Everything (HDD)\Misc\Gooseworks (Projects)\FFXIV-Development"
$old = Join-Path $parent "Franthropy.Dalamud"
$new = Join-Path $parent "Franthropy"
if (Test-Path -LiteralPath $new) {
    throw "Target folder already exists: $new"
}
Move-Item -LiteralPath $old -Destination $new
```

Expected: `Franthropy` exists and `Franthropy.Dalamud` no longer exists.

- [ ] **Step 3: Verify renamed checkout**

Run:

```powershell
git -C "F:\Everything (HDD)\Misc\Gooseworks (Projects)\FFXIV-Development\Franthropy" status -sb
dotnet test "F:\Everything (HDD)\Misc\Gooseworks (Projects)\FFXIV-Development\Franthropy\Franthropy.sln" -c Debug
```

Expected: Git status is clean and tests pass.

---

### Task 6: Migrate CMB Project References To Standalone Franthropy

**Files:**
- Modify: `F:\Everything (HDD)\Misc\Gooseworks (Projects)\FFXIV-Development\ComplicatedMarketBoard\ComplicatedMarketBoard\ComplicatedMarketBoard.csproj`
- Modify: `F:\Everything (HDD)\Misc\Gooseworks (Projects)\FFXIV-Development\ComplicatedMarketBoard\ComplicatedMarketBoard.sln`

- [ ] **Step 1: Write the CMB project property and clear error**

In `ComplicatedMarketBoard/ComplicatedMarketBoard.csproj`, replace:

```xml
<ProjectReference Include="..\Franthropy.Dalamud\Franthropy.Dalamud.csproj" />
```

with:

```xml
<PropertyGroup>
  <FranthropyDalamudProject Condition="'$(FranthropyDalamudProject)' == ''">..\..\Franthropy\src\Franthropy.Dalamud\Franthropy.Dalamud.csproj</FranthropyDalamudProject>
</PropertyGroup>

<Target Name="ValidateFranthropyDalamudProject" BeforeTargets="ResolveReferences">
  <Error
    Condition="!Exists('$(FranthropyDalamudProject)')"
    Text="Franthropy.Dalamud project was not found at '$(FranthropyDalamudProject)'. Clone Franthropy beside ComplicatedMarketBoard or set -p:FranthropyDalamudProject=&quot;path\to\Franthropy.Dalamud.csproj&quot;." />
</Target>
```

Then keep the existing item group but change the Franthropy reference to:

```xml
<ProjectReference Include="$(FranthropyDalamudProject)" />
```

Expected: CMB has a default sibling-checkout reference that can be overridden.

- [ ] **Step 2: Update CMB solution project path**

In `ComplicatedMarketBoard.sln`, replace the Franthropy project line:

```text
Project("{9A19103F-16F7-4668-BE54-9A1E7A4F7556}") = "Franthropy.Dalamud", "Franthropy.Dalamud\Franthropy.Dalamud.csproj", "{0C2AC325-C013-4ED8-B1A3-C72FD2CADFFA}"
```

with:

```text
Project("{9A19103F-16F7-4668-BE54-9A1E7A4F7556}") = "Franthropy.Dalamud", "..\Franthropy\src\Franthropy.Dalamud\Franthropy.Dalamud.csproj", "{0C2AC325-C013-4ED8-B1A3-C72FD2CADFFA}"
```

Expected: Solution builds with Franthropy as a sibling checkout.

- [ ] **Step 3: Build CMB Debug**

Run:

```powershell
dotnet build "F:\Everything (HDD)\Misc\Gooseworks (Projects)\FFXIV-Development\ComplicatedMarketBoard\ComplicatedMarketBoard.sln" -c Debug --no-incremental
```

Expected: Build succeeds and CMB build output contains `Franthropy.Dalamud.dll`.

- [ ] **Step 4: Verify override path works**

Run:

```powershell
dotnet build "F:\Everything (HDD)\Misc\Gooseworks (Projects)\FFXIV-Development\ComplicatedMarketBoard\ComplicatedMarketBoard\ComplicatedMarketBoard.csproj" -c Debug --no-incremental -p:FranthropyDalamudProject="F:\Everything (HDD)\Misc\Gooseworks (Projects)\FFXIV-Development\Franthropy\src\Franthropy.Dalamud\Franthropy.Dalamud.csproj"
```

Expected: Build succeeds when the path is supplied explicitly.

---

### Task 7: Remove CMB Embedded Franthropy Submodule/Nested Repo Safely

**Files:**
- Modify: `F:\Everything (HDD)\Misc\Gooseworks (Projects)\FFXIV-Development\ComplicatedMarketBoard\.gitmodules`
- Remove: `F:\Everything (HDD)\Misc\Gooseworks (Projects)\FFXIV-Development\ComplicatedMarketBoard\Franthropy.Dalamud`
- Inspect/remove if safe: `F:\Everything (HDD)\Misc\Gooseworks (Projects)\FFXIV-Development\ComplicatedMarketBoard\.git\modules\Franthropy.Dalamud`

- [ ] **Step 1: Re-run nested repo safety checks immediately before deletion**

Run:

```powershell
$cmb = "F:\Everything (HDD)\Misc\Gooseworks (Projects)\FFXIV-Development\ComplicatedMarketBoard"
git -C $cmb submodule status
git -C $cmb ls-files -s Franthropy.Dalamud
git -C "$cmb\Franthropy.Dalamud" status --short
git -C "$cmb\Franthropy.Dalamud" log -1 --oneline
git -C "F:\Everything (HDD)\Misc\Gooseworks (Projects)\FFXIV-Development\Franthropy" log -1 --oneline
```

Expected: Nested repo status is clean and any HEAD mismatch is understood. If `status --short` has output, stop and preserve or commit that work before deleting.

- [ ] **Step 2: Remove Franthropy entry from `.gitmodules`**

Change `.gitmodules` from:

```ini
[submodule "Miosuke"]
	path = Miosuke
	url = https://github.com/Elypha/Miosuke.git
[submodule "Franthropy.Dalamud"]
	path = Franthropy.Dalamud
	url = https://github.com/FranFkntastic/Franthropy.Dalamud.git
```

to:

```ini
[submodule "Miosuke"]
	path = Miosuke
	url = https://github.com/Elypha/Miosuke.git
```

Expected: `.gitmodules` no longer mentions Franthropy.

- [ ] **Step 3: Remove CMB Franthropy folder**

Run:

```powershell
$cmb = "F:\Everything (HDD)\Misc\Gooseworks (Projects)\FFXIV-Development\ComplicatedMarketBoard"
$target = [System.IO.Path]::GetFullPath((Join-Path $cmb "Franthropy.Dalamud"))
$expectedRoot = [System.IO.Path]::GetFullPath($cmb)
if (-not $target.StartsWith($expectedRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to remove path outside CMB: $target"
}
Remove-Item -LiteralPath $target -Recurse -Force
```

Expected: `ComplicatedMarketBoard/Franthropy.Dalamud` is gone.

- [ ] **Step 4: Remove stale module metadata if it exists and was verified safe**

Run:

```powershell
$cmb = "F:\Everything (HDD)\Misc\Gooseworks (Projects)\FFXIV-Development\ComplicatedMarketBoard"
$modulePath = Join-Path $cmb ".git\modules\Franthropy.Dalamud"
if (Test-Path -LiteralPath $modulePath) {
    Remove-Item -LiteralPath $modulePath -Recurse -Force
}
```

Expected: `.git/modules/Franthropy.Dalamud` is gone if it existed.

- [ ] **Step 5: Check CMB status**

Run:

```powershell
git -C "F:\Everything (HDD)\Misc\Gooseworks (Projects)\FFXIV-Development\ComplicatedMarketBoard" status --short
```

Expected: `.gitmodules`, `ComplicatedMarketBoard.sln`, and `ComplicatedMarketBoard/ComplicatedMarketBoard.csproj` are modified. `Franthropy.Dalamud/` is not present as an untracked folder.

---

### Task 8: Verify CMB Build And Release Package

**Files:**
- Verify: `F:\Everything (HDD)\Misc\Gooseworks (Projects)\FFXIV-Development\ComplicatedMarketBoard\dist\latest.zip`
- Verify: `F:\Everything (HDD)\Misc\Gooseworks (Projects)\FFXIV-Development\ComplicatedMarketBoard\tools\Build-DalamudRelease.ps1`

- [ ] **Step 1: Run CMB Debug build**

Run:

```powershell
dotnet build "F:\Everything (HDD)\Misc\Gooseworks (Projects)\FFXIV-Development\ComplicatedMarketBoard\ComplicatedMarketBoard.sln" -c Debug --no-incremental
```

Expected: Build succeeds.

- [ ] **Step 2: Run CMB Release build**

Run:

```powershell
dotnet build "F:\Everything (HDD)\Misc\Gooseworks (Projects)\FFXIV-Development\ComplicatedMarketBoard\ComplicatedMarketBoard.sln" -c Release --no-incremental
```

Expected: Build succeeds.

- [ ] **Step 3: Run CMB release package script**

Run:

```powershell
Set-Location "F:\Everything (HDD)\Misc\Gooseworks (Projects)\FFXIV-Development\ComplicatedMarketBoard"
.\tools\Build-DalamudRelease.ps1
```

Expected: Script succeeds and creates `dist/latest.zip`.

- [ ] **Step 4: Inspect release package contents**

Run:

```powershell
$zip = "F:\Everything (HDD)\Misc\Gooseworks (Projects)\FFXIV-Development\ComplicatedMarketBoard\dist\latest.zip"
Add-Type -AssemblyName System.IO.Compression.FileSystem
$entries = [System.IO.Compression.ZipFile]::OpenRead($zip).Entries.FullName
$entries | Sort-Object
if ($entries -notcontains "Franthropy.Dalamud.dll") { throw "Franthropy.Dalamud.dll missing from package." }
if ($entries -notcontains "Franthropy.Dalamud.xml") { throw "Franthropy.Dalamud.xml missing from package." }
```

Expected: Output includes `Franthropy.Dalamud.dll` and `Franthropy.Dalamud.xml`.

- [ ] **Step 5: Run CMB whitespace check**

Run:

```powershell
git -C "F:\Everything (HDD)\Misc\Gooseworks (Projects)\FFXIV-Development\ComplicatedMarketBoard" diff --check
```

Expected: No whitespace errors.

---

### Task 9: Commit CMB Migration

**Files:**
- Commit in CMB repo:
  - `.gitmodules`
  - `ComplicatedMarketBoard.sln`
  - `ComplicatedMarketBoard/ComplicatedMarketBoard.csproj`
  - removal of `Franthropy.Dalamud/` if tracked by Git

- [ ] **Step 1: Review CMB diff**

Run:

```powershell
git -C "F:\Everything (HDD)\Misc\Gooseworks (Projects)\FFXIV-Development\ComplicatedMarketBoard" diff -- .gitmodules ComplicatedMarketBoard.sln ComplicatedMarketBoard\ComplicatedMarketBoard.csproj
git -C "F:\Everything (HDD)\Misc\Gooseworks (Projects)\FFXIV-Development\ComplicatedMarketBoard" status --short
```

Expected: Diff only contains Franthropy reference/submodule migration plus any pre-existing CMB changes that were intentionally part of this branch. Do not stage unrelated dirty files.

- [ ] **Step 2: Stage CMB migration files**

Run:

```powershell
git -C "F:\Everything (HDD)\Misc\Gooseworks (Projects)\FFXIV-Development\ComplicatedMarketBoard" add .gitmodules ComplicatedMarketBoard.sln ComplicatedMarketBoard\ComplicatedMarketBoard.csproj
```

If Git reports tracked removals under `Franthropy.Dalamud`, stage them:

```powershell
git -C "F:\Everything (HDD)\Misc\Gooseworks (Projects)\FFXIV-Development\ComplicatedMarketBoard" add -u Franthropy.Dalamud
```

Expected: Staged changes are limited to the CMB migration.

- [ ] **Step 3: Commit CMB migration**

Run:

```powershell
git -C "F:\Everything (HDD)\Misc\Gooseworks (Projects)\FFXIV-Development\ComplicatedMarketBoard" commit -m "chore: reference standalone franthropy library"
```

Expected: Commit succeeds in CMB repo.

---

### Task 10: Final Verification And Handoff

**Files:**
- Verify all changed repos.

- [ ] **Step 1: Verify Franthropy final state**

Run:

```powershell
git -C "F:\Everything (HDD)\Misc\Gooseworks (Projects)\FFXIV-Development\Franthropy" status -sb
dotnet test "F:\Everything (HDD)\Misc\Gooseworks (Projects)\FFXIV-Development\Franthropy\Franthropy.sln" -c Debug
git -C "F:\Everything (HDD)\Misc\Gooseworks (Projects)\FFXIV-Development\Franthropy" diff --check
```

Expected: Franthropy is clean or only ahead of remote; tests pass; diff check clean.

- [ ] **Step 2: Verify CMB final state**

Run:

```powershell
git -C "F:\Everything (HDD)\Misc\Gooseworks (Projects)\FFXIV-Development\ComplicatedMarketBoard" status -sb
dotnet build "F:\Everything (HDD)\Misc\Gooseworks (Projects)\FFXIV-Development\ComplicatedMarketBoard\ComplicatedMarketBoard.sln" -c Debug --no-incremental
dotnet build "F:\Everything (HDD)\Misc\Gooseworks (Projects)\FFXIV-Development\ComplicatedMarketBoard\ComplicatedMarketBoard.sln" -c Release --no-incremental
Set-Location "F:\Everything (HDD)\Misc\Gooseworks (Projects)\FFXIV-Development\ComplicatedMarketBoard"
.\tools\Build-DalamudRelease.ps1
git diff --check
```

Expected: CMB builds and packages successfully. Any remaining dirty files are pre-existing unrelated work or generated `dist` artifacts that are intentionally ignored.

- [ ] **Step 3: Inspect package one final time**

Run:

```powershell
$zip = "F:\Everything (HDD)\Misc\Gooseworks (Projects)\FFXIV-Development\ComplicatedMarketBoard\dist\latest.zip"
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($zip)
try {
    $entries = $archive.Entries.FullName
    if ($entries -notcontains "Franthropy.Dalamud.dll") { throw "Franthropy.Dalamud.dll missing from package." }
    if ($entries -notcontains "Franthropy.Dalamud.xml") { throw "Franthropy.Dalamud.xml missing from package." }
}
finally {
    $archive.Dispose()
}
```

Expected: No exception.

- [ ] **Step 4: Report final commits and unresolved decisions**

Final response should include:

- Franthropy commit ids created.
- CMB commit id created.
- Whether the local folder was renamed from `Franthropy.Dalamud` to `Franthropy`.
- Whether the GitHub repository/remote URL was renamed or left as `Franthropy.Dalamud`.
- Verification commands and pass/fail status.
- Any remaining dirty files that predated the migration.

---

## Self-Review Notes

- Spec coverage: Covers standalone repo restructure, tests, CMB reference migration, submodule cleanup, package verification, CA deferral, and MarketMafioso no-op scope.
- Scope guard: Does not move MarketMafioso route code, purchase automation, Craft Architect code, `Franthropy.Markets`, or `Franthropy.Dalamud.MarketBoard`.
- Path consistency: Uses `FranthropyDalamudProject` consistently for the CMB MSBuild property.
- CMB dirty-state risk: Task 1 requires explicit inspection before CMB edits.
- Submodule deletion risk: Task 7 requires nested repo status and HEAD checks before deletion.
