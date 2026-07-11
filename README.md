# Franthropy

Shared FFXIV toolkit libraries for Franthropy plugins and tools.

## Projects

- `src/Franthropy.Dalamud` - Dalamud-aware helper primitives such as world catalog lookups and Lifestream market-board travel command construction.

## Current Scope

The toolkit scope is intentionally small:

- world catalog lookup
- Lifestream market-board travel command construction
- immutable character and equipment observation contracts
- neutral equipment-use and gearset-protection analysis
- frame-validated UI review primitives

Product policy, workflow orchestration, automation decisions, and application-specific integration remain in their owning plugins.

## Reuse Maxim

Before adding a type or subsystem to Franthropy, ask:

> Would at least two unrelated plugins reasonably use this without inheriting another product's architecture or policy?

If the answer is not clearly yes, keep the code in its owning plugin until a second credible consumer proves the shared boundary.

This applies even when moving code into Franthropy would reduce duplication in the short term. Franthropy is a toolkit of proven neutral primitives, not a general plugin-suite service bus, cross-plugin policy layer, or holding area for code that merely feels infrastructural.

Shared code should therefore:

- remain neutral about product names, workflows, and user policy;
- expose explicit contracts and diagnostics rather than permissive fallbacks;
- avoid making unrelated consumers adopt one plugin's lifecycle or transport architecture;
- be promoted from a plugin only when the common abstraction is demonstrated;
- stay in a focused namespace or project so consumers reference only what they need.

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
