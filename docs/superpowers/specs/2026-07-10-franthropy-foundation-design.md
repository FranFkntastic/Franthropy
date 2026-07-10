# Franthropy Foundation Design

## Goal

Turn the standalone `Franthropy.Dalamud` seed repository into the canonical `Franthropy` shared toolkit repository, then remove the vestigial shared-library copy from the ComplicatedMarketBoard repository.

This is a foundation pass. It creates the durable repo and project shape that later MarketMafioso, ComplicatedMarketBoard, and other FFXIV tools can consume. It does not move MarketMafioso route orchestration, purchase automation, Craft Architect contracts, or Craft Architect implementation code yet.

## Current State

There is already a standalone repository at:

`F:\Everything (HDD)\Misc\Gooseworks (Projects)\FFXIV-Development\Franthropy.Dalamud`

It currently contains one project, `Franthropy.Dalamud`, with:

- `Worlds/WorldCatalog.cs`
- `Travel/LifestreamTravelCommandBuilder.cs`
- `Travel/WorldTravelRequest.cs`
- `Travel/WorldTravelCommandResult.cs`

ComplicatedMarketBoard also contains a vestigial embedded submodule under:

`F:\Everything (HDD)\Misc\Gooseworks (Projects)\FFXIV-Development\ComplicatedMarketBoard\Franthropy.Dalamud`

The submodule is also listed in `ComplicatedMarketBoard\.gitmodules`. The embedded submodule should stop being a source of truth. Franthropy should own shared toolkit code in its own repository, and CMB should reference that standalone repository directly during local development.

## Architecture Direction

Franthropy is the reusable toolkit. Plugins and apps keep their product-specific workflows.

For the foundation phase, Franthropy should provide only the existing shared world/travel engine parts:

- world/catalog/scope primitives
- Lifestream travel command construction

Later phases may add additional reusable engine parts:

- market-board listing/read models
- listing coverage and read-quality vocabulary
- live listing identity and revalidation primitives
- market-board purchase execution primitives
- purchase confirmation/removal monitoring primitives
- generic automation outcomes and diagnostics snapshots

Products keep their fuel line and transmission:

- MarketMafioso keeps acquisition route policy, dashboard lifecycle, opportunistic buy policy, recent-world TTL decisions, and route audit semantics.
- ComplicatedMarketBoard keeps market browsing, scope UX, pricing UI, and any CMB-specific presentation.
- Craft Architect consolidation is explicitly deferred.

## Repository Shape

Rename or conceptually evolve the standalone repository from `Franthropy.Dalamud` to `Franthropy`.

The foundation layout should become:

```text
Franthropy/
  src/
    Franthropy.Dalamud/
      Travel/
      Worlds/
      Franthropy.Dalamud.csproj
  tests/
    Franthropy.Dalamud.Tests/
      Travel/
      Worlds/
      Franthropy.Dalamud.Tests.csproj
  docs/
    superpowers/
      specs/
  Franthropy.sln
  README.md
  LICENSE
```

The current project name `Franthropy.Dalamud` should remain. The repository name should broaden so future non-Dalamud projects do not live under a misleading repo identity.

The local checkout folder should be renamed or recloned to:

`F:\Everything (HDD)\Misc\Gooseworks (Projects)\FFXIV-Development\Franthropy`

This parent-folder rename is not represented in Git history, but it should be part of the local migration so future project references and docs do not keep the old repo identity alive.

If the GitHub repository is renamed from `Franthropy.Dalamud` to `Franthropy`, the implementation must also update:

- `origin` remote URL
- project metadata such as `PackageProjectUrl`
- README repository references
- any consumer docs that name the old repository

If the GitHub repository is not renamed during the first pass, the implementation must explicitly document that only the local checkout and solution identity changed.

## Future Project Boundaries

These projects are expected later, but are not required in the foundation pass:

```text
src/
  Franthropy.Core/
  Franthropy.Markets/
  Franthropy.Dalamud/
  Franthropy.Dalamud.MarketBoard/
```

Dependency direction:

```text
Franthropy.Core
  no product, Dalamud, or market dependency

Franthropy.Markets
  pure market data, scopes, listings, Universalis contracts, coverage vocabulary
  no Dalamud dependency

Franthropy.Dalamud
  Dalamud-aware world, travel, addon, and game-client helper primitives
  may depend on pure Franthropy projects

Franthropy.Dalamud.MarketBoard
  live game-client market-board read and purchase automation primitives
  may depend on Franthropy.Markets and Franthropy.Dalamud
```

## Foundation Phase Scope

The first implementation phase should do only this:

1. Restructure the standalone repo into `src/Franthropy.Dalamud`.
2. Add a solution file named `Franthropy.sln`.
3. Add `tests/Franthropy.Dalamud.Tests`.
4. Add tests for existing world and travel behavior.
5. Update ComplicatedMarketBoard to reference the standalone `Franthropy.Dalamud` project through an MSBuild property, not a hard-coded absolute path.
6. Default the CMB project property to the sibling checkout path `..\..\Franthropy\src\Franthropy.Dalamud\Franthropy.Dalamud.csproj`, resolved from `ComplicatedMarketBoard/ComplicatedMarketBoard`.
7. Allow the CMB property to be overridden by local `Directory.Build.props`, command-line MSBuild properties, or CI configuration.
8. Add a clear build error if the resolved Franthropy project path does not exist.
9. Remove the `ComplicatedMarketBoard/Franthropy.Dalamud` submodule entry from `.gitmodules`.
10. Remove the `ComplicatedMarketBoard/Franthropy.Dalamud` nested repo/submodule folder only after the safety checks in the next section pass.
11. Remove stale Git module metadata for `ComplicatedMarketBoard/.git/modules/Franthropy.Dalamud` only after confirming it contains no unique work.
12. Update `ComplicatedMarketBoard.sln` to reference the standalone project path `..\Franthropy\src\Franthropy.Dalamud\Franthropy.Dalamud.csproj`, resolved from the CMB repo root.
13. Update `ComplicatedMarketBoard/ComplicatedMarketBoard.csproj` to reference the standalone project path through the property.
14. Verify `tools/Build-DalamudRelease.ps1` still packages `Franthropy.Dalamud.dll` and `Franthropy.Dalamud.xml` from CMB build output.
15. Verify both Franthropy tests and ComplicatedMarketBoard build/package flows.

## CMB Submodule Removal Safety Checks

Before deleting `ComplicatedMarketBoard/Franthropy.Dalamud`, the implementation must verify:

1. `git -C ComplicatedMarketBoard submodule status` output is understood and recorded.
2. `git -C ComplicatedMarketBoard ls-files -s Franthropy.Dalamud` is checked so the implementation knows whether CMB currently tracks a gitlink.
3. `git -C ComplicatedMarketBoard/Franthropy.Dalamud status --short` is clean.
4. The nested Franthropy HEAD matches the standalone Franthropy HEAD, or any difference is intentionally preserved before deletion.
5. The nested repo has no untracked files that need to be kept.
6. `.gitmodules` is updated in the same CMB commit that removes the nested repo path.
7. Any stale `.git/modules/Franthropy.Dalamud` metadata is removed deliberately only after the nested repo state is verified.

The foundation phase should not:

- move MarketMafioso route-runner code
- move MarketMafioso purchase automation code
- move Craft Architect API contracts or implementation
- create `Franthropy.Markets`
- create `Franthropy.Dalamud.MarketBoard`
- alter MarketMafioso behavior

## Later Phase: Market Toolkit Extraction

After the foundation phase is stable, the next candidate phase is `Franthropy.Markets`:

- market scopes
- region/data-center/world expansion
- Universalis target normalization
- shared market listing DTOs
- listing coverage/read-quality vocabulary
- observed market snapshot primitives

This should be driven by concrete consumers from MarketMafioso and ComplicatedMarketBoard, not by speculative generalization.

## Later Phase: Market Board Automation Extraction

After `Franthropy.Markets` has stable listing/read concepts, a later phase can add `Franthropy.Dalamud.MarketBoard`.

Reusable candidates:

- live market-board listing models
- listing read result and freshness state
- visible listing coverage classification
- listing identity and revalidation
- purchase adapter interface
- purchase result models
- purchase confirmation/removal session primitives
- generic automation outcomes and snapshots

MarketMafioso should still keep:

- acquisition candidate policy
- route engine and route policy
- dashboard claim/progress reporting
- route diagnostics file formats
- recent-world suppression policy
- opportunistic buy semantics

## Craft Architect Deferral

Craft Architect API consolidation is useful, but it is out of scope for this refactor.

The CA Core/Web split can be revisited later. No Craft Architect code should move into Franthropy during the foundation phase. No MarketMafioso Workshop Host or craft quote code should be changed as part of this foundation pass.

## Risks

- If the standalone Franthropy project is referenced by absolute local paths, other worktrees may become brittle. Use a CMB MSBuild property with a sibling-checkout default and an override path.
- If fresh CMB clones do not also clone Franthropy beside CMB or configure the override property, builds will fail by design with a clear missing-project error.
- If CMB keeps any hidden dependency on the embedded submodule, deleting it may break build scripts, IDE configuration, or submodule initialization workflows.
- If `.gitmodules` is not updated when the embedded submodule is removed, future clones may try to initialize a removed Franthropy submodule.
- If `ComplicatedMarketBoard.sln` keeps the removed project path, command-line builds can fail even when the plugin `.csproj` reference is updated.
- If CMB release packaging no longer copies `Franthropy.Dalamud.dll` and `Franthropy.Dalamud.xml`, the plugin may build locally but ship without its shared dependency.
- If Franthropy grows market-board automation before pure market concepts exist, it may couple reusable logic to Dalamud too early.
- If MarketMafioso route-engine extraction happens before the shared foundation stabilizes, the route refactor may be forced to chase moving dependency boundaries.

## Success Criteria

- The standalone repo is the only Franthropy source of truth.
- The standalone repo builds and tests independently.
- ComplicatedMarketBoard builds against the standalone Franthropy project.
- The embedded CMB `Franthropy.Dalamud` submodule, gitlink, and `.gitmodules` entry are gone.
- CMB release packaging still includes `Franthropy.Dalamud.dll` and `Franthropy.Dalamud.xml`.
- `.\tools\Build-DalamudRelease.ps1` succeeds from the CMB repo.
- `dist/latest.zip` contains `Franthropy.Dalamud.dll` and `Franthropy.Dalamud.xml`.
- No MarketMafioso behavior changes in the foundation phase.
- No Craft Architect behavior changes in the foundation phase.

## Implementation Decisions

- Physically rename or reclone the standalone local folder to `Franthropy` during the foundation migration.
- Use a `ProjectReference` from CMB to the standalone Franthropy checkout during active local development, mediated by an MSBuild property so CI and other local layouts can override the path.
- Defer NuGet/package consumption until the library has more than one consumer actively depending on versioned releases.

## Verification Commands

Franthropy:

```powershell
dotnet test .\Franthropy.sln -c Debug
git diff --check
```

ComplicatedMarketBoard:

```powershell
dotnet build .\ComplicatedMarketBoard.sln -c Debug --no-incremental
dotnet build .\ComplicatedMarketBoard.sln -c Release --no-incremental
.\tools\Build-DalamudRelease.ps1
git diff --check
```

If CMB gains a test project before this work is implemented, run that test project as part of verification. Until then, Debug build, Release build, package creation, zip-content inspection, and `git diff --check` are the required checks.

## Agent Review

Critical review was performed by subagent Avicenna. Findings incorporated:

- Made CMB consumption explicit as a sibling checkout `ProjectReference` mediated by an overridable MSBuild property.
- Added safety checks for removing the stale CMB Franthropy submodule/nested repo.
- Narrowed the foundation architecture wording so market-board and purchase automation remain roadmap items, not phase-one scope.
- Added repo rename metadata fallout for remote URLs, README, and package project URL.
- Added release-package verification and `dist/latest.zip` content inspection.
- Clarified that CMB currently has build/package verification rather than a dedicated test project.
