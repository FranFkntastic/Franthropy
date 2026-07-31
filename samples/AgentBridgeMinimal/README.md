# Minimal Agent Bridge Plugin

This sample is deliberately read-only. It publishes authenticated discovery,
advertises a versioned capability manifest, and serves one `get-snapshot`
command through an allowlist-by-construction router.

Inside the Franthropy repository it uses a project reference so every change is
buildable before publication. In another plugin, replace that reference with:

```xml
<PackageReference Include="Franthropy.AgentBridge" Version="0.1.0" />
```

Build the project, install it as a Dalamud development plugin, then use DAB's
`bridge_list`, `bridge_health`, `bridge_manifest`, and `bridge_snapshot` tools.
The sample exposes no action, coordinate input, chat command, or game mutation.
