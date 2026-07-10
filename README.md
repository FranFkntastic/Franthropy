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
