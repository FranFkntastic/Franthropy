# Franthropy.Dalamud

Shared Dalamud helper primitives for FranFkntastic plugins.

This is a normal .NET class library, not a Dalamud plugin. Consumer plugins reference it as a source dependency and package the built `Franthropy.Dalamud.dll` with their own plugin release.

Initial helpers cover:

- Canonical FFXIV world lookup from host-provided world records.
- Lifestream market-board travel command building.
- Explicit operation result types for command handoff.

