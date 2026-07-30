# Franthropy.AgentBridge

`Franthropy.AgentBridge` provides the small, product-neutral core used by
Dalamud Agent Bridge integrations:

- versioned discovery, manifest, action, operation, and receipt contracts;
- a current-user authenticated named-pipe host;
- an allowlist-by-construction command router;
- frame-bound reviewed control registration and invocation;
- reversible UI capture transaction state; and
- Windows DPAPI helpers for local access tokens and capture handoffs.

The package deliberately does not reference Dalamud, ImGui, ECommons, or
product plugins. Dalamud-specific window discovery, rendering, and capture
adapters remain in `Franthropy.Dalamud`.

See the [agent bridge getting-started guide](https://github.com/FranFkntastic/Franthropy/blob/main/docs/agent-bridge/getting-started.md)
and the
[minimal sample plugin](https://github.com/FranFkntastic/Franthropy/tree/main/samples/AgentBridgeMinimal).
