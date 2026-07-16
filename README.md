# Franthropy

Shared FFXIV toolkit libraries for Franthropy plugins and tools.

## Projects

- `src/Franthropy.Filtering` - dependency-free filter syntax, diagnostics, typed binding, and evaluation primitives.
- `src/Franthropy.FFXIV` - canonical, product-neutral FFXIV filter vocabulary and resolver contracts.
- `src/Franthropy.Dalamud` - Dalamud-aware helper primitives such as world catalog lookups and Lifestream market-board travel command construction.

## Current Scope

The toolkit scope is intentionally small:

- shared filter-language syntax, typed semantics, diagnostics, and generated references
- canonical FFXIV item, instance, ownership, offer, and acquisition vocabulary
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

## Design Documents

- [Franthropy Filter Language](docs/design/filter-language.md) - proposed shared filtering engine, canonical FFXIV vocabulary, context binding model, diagnostics, and staged delivery plan.
- [Canonical FFXIV Filter Vocabulary](docs/design/filter-vocabulary.md) - field semantics, named values, context availability, worked expressions, and vocabulary contribution rules.
- [Filter Language and Inventory Viewer Implementation Roadmap](docs/design/filter-language-implementation.md) - living cross-repository sequence, Inventory Viewer upgrades, acceptance gates, testing, and decision log.
