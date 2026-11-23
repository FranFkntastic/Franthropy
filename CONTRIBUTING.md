# Contributing to Franthropy

Franthropy exists to make proven, product-neutral FFXIV and Dalamud primitives
reusable across unrelated plugins. Bug reports, focused new primitives,
documentation, compatibility fixes, and tests are welcome.

## Choose the Right Boundary

Before proposing a shared type, identify at least two credible consumers that
can use it without adopting another plugin's workflow, policy, persistence, or
transport. Keep product-specific orchestration in the consuming plugin.

- `Franthropy.Filtering` owns dependency-free language mechanics.
- `Franthropy.FFXIV` owns product-neutral FFXIV domain contracts.
- `Franthropy.AgentBridge` owns transport-safe contracts, hosting, reviewed
  controls, and operation receipts without a Dalamud dependency.
- `Franthropy.Dalamud` owns game- and Dalamud-facing adapters and UI primitives.
- `Franthropy.Web` owns web-facing adapters for shared models.

Prefer a focused namespace and explicit contract over a broad helper class.
Patch-sensitive mechanics must fail closed when their supported game or Dalamud
contract cannot be proven.

## Branch and Pull Request Flow

Create your branch from `main` and target `main` in the pull request.
Open an issue first for a new project, breaking API change, native game
interaction, or major dependency. Small fixes can go directly to a pull request.

Describe the demonstrated consumers, the ownership boundary, compatibility
impact, and the smallest checks that prove the change. Public API changes should
include focused tests and usage documentation.

Never commit game logs, crash bundles, screenshots, player data, access tokens,
plugin configuration, or machine-specific paths.

## Verification

Build and test the projects affected by your change. The solution includes
projects targeting .NET 8 and .NET 10; changes to `Franthropy.Dalamud` also
require a local Dalamud development environment.

Note in your pull request what was verified via automated checks versus tested
in-game.

By contributing, you agree that your contribution is licensed under GPL-3.0,
the repository's existing license.
