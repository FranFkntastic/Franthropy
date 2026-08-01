# Franthropy

Shared FFXIV toolkit libraries for Franthropy plugins and tools.

## Projects

- `src/Franthropy.AgentBridge` - authenticated agent-bridge wire contracts, hosting, reviewed controls, and operation receipts without a Dalamud dependency.
- `src/Franthropy.Filtering` - dependency-free filter syntax, diagnostics, typed binding, and evaluation primitives.
- `src/Franthropy.FFXIV` - canonical, product-neutral FFXIV filter vocabulary and resolver contracts.
- `src/Franthropy.Observations` - versioned shared observation contracts, truthful-state validation, and SQLite persistence.
- `src/Franthropy.Dalamud` - Dalamud-aware helper primitives such as world catalog lookups and Lifestream market-board travel command construction.
- `src/Franthropy.Web` - web-facing adapters for shared Franthropy models.

## Current Scope

The toolkit scope is intentionally small:

- shared filter-language syntax, typed semantics, diagnostics, and generated references
- canonical FFXIV item, instance, ownership, offer, and acquisition vocabulary
- world catalog lookup
- Lifestream market-board travel command construction
- immutable character and equipment observation contracts
- durable owner-scoped inventory and retainer observation contracts
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

## NuGet packages

Versioned packages are published for the focused, independently reusable
projects:

```powershell
dotnet add package Franthropy.AgentBridge
dotnet add package Franthropy.Filtering
dotnet add package Franthropy.FFXIV
dotnet add package Franthropy.Observations
dotnet add package Franthropy.Web
```

`Franthropy.Dalamud` remains source-consumed because its broad, patch-sensitive
surface compiles against the developer's current Dalamud installation. Agent
bridge contracts and hosting no longer require that monolith.

See [Agent Bridge: Getting Started](docs/agent-bridge/getting-started.md) for a
minimal plugin integration and [Release Process](docs/releases.md) for package
versioning and publication.

## Consuming source

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

The repository requires the .NET 8 and .NET 10 SDKs. `Franthropy.Dalamud`
also expects a development Dalamud installation.

```powershell
dotnet test .\Franthropy.sln -c Debug
```

## Design Documents

- [Franthropy Filter Language](docs/design/filter-language.md) - proposed shared filtering engine, canonical FFXIV vocabulary, context binding model, diagnostics, and staged delivery plan.
- [Canonical FFXIV Filter Vocabulary](docs/design/filter-vocabulary.md) - field semantics, named values, context availability, worked expressions, and vocabulary contribution rules.
- [Retainer Automation Sessions](docs/design/retainer-automation.md) - shared retainer discovery, inventory transfer, selling-list mutation, exact-evidence, and indeterminate-outcome contracts.

## Contributing

Pull requests are welcome. Start with [CONTRIBUTING.md](CONTRIBUTING.md), create
changes from `main`, and keep each shared primitive
small enough for an unrelated consumer to adopt without inheriting product
policy. Report vulnerabilities privately as described in
[SECURITY.md](SECURITY.md).

Franthropy is licensed under the
[GNU General Public License v3.0](LICENSE).
