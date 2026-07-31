# Agent Bridge: Getting Started

`Franthropy.AgentBridge` is the product-neutral SDK beneath DAB's authenticated
plugin protocol. It lets a plugin advertise what an agent may observe or invoke
without exposing arbitrary reflection, coordinate input, or unrestricted
methods.

## Install

Add the package to a Windows Dalamud plugin project:

```powershell
dotnet add package Franthropy.AgentBridge --version 0.1.0
```

The package targets `net8.0-windows` and `net10.0-windows`. It references no
Dalamud, ImGui, ECommons, or product-plugin assembly.

## Host a read-only bridge

The smallest useful integration has four parts:

1. Persist a stable plugin-instance ID and DPAPI-protected access token.
2. Create one stable runtime identity when the plugin loads.
3. Advertise a versioned manifest describing available capabilities.
4. Route only explicitly registered commands.

```csharp
using Franthropy.Dalamud.AgentBridge;

var profile = AgentBridgeProfileIdentity.FromPluginConfigDirectory(configDirectory);
var runtime = AgentBridgeRuntimeIdentity.FromAssembly(
    "ExamplePlugin",
    typeof(Plugin).Assembly,
    mainDllPath);

var router = new AgentBridgeCommandRouter()
    .Register("get-snapshot", _ => AgentBridgeResponse.Ok(
        "Snapshot captured.",
        new { schema = "example.snapshot.v1", enabled = true }));

var host = new AgentBridgeHost(new AgentBridgeHostOptions
{
    ConfigDirectory = configDirectory,
    PluginInstanceId = configuration.PluginInstanceId,
    PipeName = $"example.agentbridge.{Environment.ProcessId}.{configuration.PluginInstanceId}",
    GetProtectedAccessToken = () => configuration.ProtectedAccessToken,
    SetProtectedAccessToken = value => configuration.ProtectedAccessToken = value,
    SaveConfiguration = SaveConfiguration,
    CreateManifest = () => new AgentBridgeManifest(
        ProtocolVersion: 1,
        Runtime: runtime,
        ProfileId: profile.Id,
        ProfileAlias: profile.Alias,
        SnapshotSchema: "example.snapshot.v1",
        Capabilities: [new AgentBridgeCapabilityDescriptor("snapshot.read")],
        ReviewSurfaces: [],
        CaptureSurfaces: [],
        Actions: []),
    HandleRequestAsync = router.HandleAsync,
});

host.Start();
```

Dispose the host with the plugin. It removes its discovery advertisement and
stops accepting named-pipe requests.

The complete
[AgentBridgeMinimal sample](../../samples/AgentBridgeMinimal/README.md) is a
buildable Dalamud plugin using this exact pattern.

## Add reviewed controls

`AgentBridgeUiReviewRegistry` records only controls actually rendered in the
current ImGui frame. Each registered control has a stable semantic ID, enabled
state, optional typed argument schema, and invocation delegate.

Call `BeginFrame()` before rendering, register controls as they render, and call
`EndFrame()` afterward. Invocation requires the expected frame ID; expired,
missing, disabled, or replayed controls fail closed.

Dalamud-specific convenience methods such as `RegisterLastButton` remain in
`Franthropy.Dalamud`. The core registry stays UI-library-neutral.

## Report long-running work

Use `AgentBridgeOperationRegistry` when an action outlives the reviewed frame.
Return its operation ID in `AgentBridgeResponse`, then update the registry
through queued, running, and one terminal state. Structured postconditions let
an agent verify the result without sleeps or pixel guesses.

## Compatibility rules

- Increment capability versions when their meaning or required fields change.
- Use a new snapshot schema identifier for incompatible snapshot changes.
- Keep command and action IDs stable after publication.
- Treat discovery process IDs, runtime instance IDs, frame IDs, and viewport
  IDs as ephemeral runtime data.
- Never infer mutation authority from a reflected window or serialized field.

The protocol is local and authenticated, but local transport is not a reason to
weaken allowlists, expiry, replay protection, or validation.
