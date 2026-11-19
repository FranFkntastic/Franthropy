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

### Same-user peer links

`Franthropy.PeerLink` is independent of the agent host. On each client, await
`PeerEndpoint.StartAsync(options, identity)` once at load. The options take an
application name, a dispatcher that completes **after** its action has run
(and honors cancellation on unload), a synchronous bounded message handler,
and a thread-safe Warning logger. The handler receives the authenticated sender
instance ID, kind and JSON payload; return `PeerResult.Delivered` or
`PeerResult.Refused`. Enforce opt-in and authorization inside that handler.
Update the opaque presence with `UpdateIdentityAsync` when it changes.

```csharp
// Receiver has already started its own endpoint under the same application name.
await using var sender = await PeerEndpoint.StartAsync(options, localIdentity);
var peers = await sender.DiscoverAsync();
var link = sender.Connect(peers.Single(p => p.InstanceId == selectedInstance));
var result = await link.RequestAsync(stableRequestId, payload, "ReceiptDeadline",
    TimeSpan.FromSeconds(8), cancellation);
```

Connect tracks that exact instance until endpoint disposal; a restarted process
gets a new instance ID and must be selected again. Subscribe to
`StateChanged` and read `State` for current status. Heartbeats use authenticated
probes every two seconds with a ten-second deadline, a five-second handshake
limit, and reconnect backoff capped at eight seconds. Each request or event
uses its own authenticated pipe. Events receive a transport acknowledgement
or refusal, not a separate application request. No application send is retried
automatically. Timeout, cancellation and link loss **do not undo remote work**.
Always await `DisposeAsync` at unload without synchronously blocking the dispatcher.

Secrets and presence live under
`%LOCALAPPDATA%\Franthropy\PeerLink\<SHA256(application)>\`. The shared
`key.dpapi` is DPAPI CurrentUser-protected and atomically published;
`<instance-id>.json` is discovery metadata, not a secret or proof of identity.
The server ACL grants only the current user and denies network logons. Both
ends verify fresh role- and instance-bound HMAC-SHA256 proofs. Another process
running as this user can read the key: this does not attest plugin code, user
consent, or in-game identity. Do not put secrets in presence.

Duplicate request results are kept for five minutes after handler completion,
keyed by sender instance and request ID. In-flight entries do not expire.
Capacity is 4096; a full cache refuses new requests rather than evicting
unexpired results. Replay protection is not durable across process restarts.
Late replies are watched for up to five minutes and logged, never applied.
