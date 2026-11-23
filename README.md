# Franthropy

Modular .NET libraries for Final Fantasy XIV tools and Dalamud plugins.

Franthropy provides shared, product-neutral building blocks—including a typed filter language, canonical FFXIV domain models, SQLite-backed inventory and character observations, local Agent Bridge hosting, and Dalamud UI helpers—so plugins and companion tools can share common primitives without coupling to each other.

## Libraries

| Project | Description |
| --- | --- |
| `src/Franthropy.Filtering` | Dependency-free filter query parser, diagnostics, typed binding, and evaluator. |
| `src/Franthropy.FFXIV` | Canonical FFXIV domain vocabulary and resolver contracts (items, worlds, ownership, offers, acquisition). |
| `src/Franthropy.Observations` | Versioned character, equipment, inventory, and retainer observation contracts with SQLite persistence. |
| `src/Franthropy.AgentBridge` | Local authenticated named-pipe bridge contracts, hosting, reviewed controls, and operation receipts (no Dalamud dependency). |
| `src/Franthropy.Dalamud` | Dalamud and ImGui helpers, world catalog lookups, Lifestream travel commands, and UI review primitives. |
| `src/Franthropy.Web` | Web-facing adapters for shared Franthropy models. |

## Installing

Versioned NuGet packages are published for the standalone libraries:

```powershell
dotnet add package Franthropy.AgentBridge
dotnet add package Franthropy.Filtering
dotnet add package Franthropy.FFXIV
dotnet add package Franthropy.Observations
dotnet add package Franthropy.Web
```

`Franthropy.Dalamud` compiles against a local Dalamud development installation and is consumed as a project reference alongside a sibling checkout:

```xml
<ProjectReference Include="..\Franthropy\src\Franthropy.Dalamud\Franthropy.Dalamud.csproj" />
```

## Building from Source

- .NET 8 and .NET 10 SDKs
- A local Dalamud development installation (required only for `Franthropy.Dalamud`)

```powershell
dotnet build Franthropy.sln
dotnet test Franthropy.sln
```

## Documentation

- [Agent Bridge: Getting Started](docs/agent-bridge/getting-started.md) — minimal plugin integration guide and [sample plugin](samples/AgentBridgeMinimal/README.md)
- [Franthropy Filter Language](docs/design/filter-language.md) — shared filtering engine, context binding, and diagnostics
- [Canonical FFXIV Filter Vocabulary](docs/design/filter-vocabulary.md) — field semantics, named values, and worked expressions
- [Retainer Automation Sessions](docs/design/retainer-automation.md) — shared retainer discovery, transfer, and selling-list contracts
- [Release Process](docs/releases.md) — package versioning and publication

## Contributing

Pull requests and issues are welcome. See [CONTRIBUTING.md](CONTRIBUTING.md) for guidelines and [SECURITY.md](SECURITY.md) for reporting vulnerabilities.

## License

Franthropy is licensed under the [GNU General Public License v3.0](LICENSE).
