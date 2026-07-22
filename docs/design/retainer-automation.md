# Retainer Automation Sessions

`Franthropy.Dalamud.Automation.Retainers` provides complete game-facing retainer interaction mechanics without requiring a product plugin such as Quartermaster.

## Responsibility

`IRetainerAutomationSession` owns the mechanics of one bounded retainer interaction:

- open the retainer list through a nearby summoning bell;
- select and verify a retainer by stable ID and display name;
- open, scan, and close the retainer inventory;
- retrieve an exact live slot and verify matching source/destination deltas;
- deposit elemental crystals and verify the resulting quantities;
- close or cancel the active interaction safely.

The consuming plugin remains responsible for deciding what to move, obtaining user authorization, coordinating with other automation, choosing retry policy, and persisting plans or receipts.

## Consumption

Construct `DalamudRetainerAutomationSession` from the standard Dalamud services and program against `IRetainerAutomationSession`. A typical reviewed workflow is:

```csharp
var ready = await session.EnsureRetainerListAsync(cancellationToken);
var opened = ready.Success
    ? await session.OpenRetainerAsync(new(retainerId, retainerName), cancellationToken)
    : ready;
var inventory = opened.Success
    ? await session.OpenInventoryAsync(cancellationToken)
    : opened;

if (inventory.Success)
{
    var stacks = await session.ScanRetainerAsync(itemIds, cancellationToken);
    // The product chooses an exact stack and authorized quantity here.
}
```

All operational actions return stable codes and diagnostic messages. Cancellation is never converted into an ordinary failure result. Transfer success requires inventory evidence; a closed menu or missing dialog is not sufficient proof.

Quartermaster is a consumer of this API and adds owner-scoped cache, target plans, review, operation persistence, recovery, and receipts. Plugins that need different product semantics may consume Franthropy directly.
