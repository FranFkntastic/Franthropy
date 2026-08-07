using System.Runtime.InteropServices;
using Dalamud.Game.Inventory;
using Dalamud.Game.Inventory.InventoryEventArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Franthropy.Dalamud.Automation.Inventory;
using Franthropy.Dalamud.Diagnostics;
using GameInventoryType = Dalamud.Game.Inventory.GameInventoryType;

namespace Franthropy.Dalamud.Automation.Retainers;

internal enum RetainerRetrievalCommand : long
{
    RetrieveFromRetainer = 0,
    RetrieveQuantity = 3,
}

internal sealed record RetainerRetrievalCommandSelection(
    RetainerRetrievalCommand Command,
    bool NeedsQuantityInput);

internal static class RetainerRetrievalCommandPolicy
{
    public static RetainerRetrievalCommandSelection Select(
        bool isCrystalContainer,
        int sourceQuantity,
        int requestedQuantity)
    {
        var transfer = Math.Min(sourceQuantity, requestedQuantity);
        if (isCrystalContainer)
            return new(RetainerRetrievalCommand.RetrieveFromRetainer, true);

        return transfer < sourceQuantity
            ? new(RetainerRetrievalCommand.RetrieveQuantity, true)
            : new(RetainerRetrievalCommand.RetrieveFromRetainer, false);
    }
}

/// <summary>
/// Retrieves an exact quantity from the currently open retainer using the
/// game's typed retainer command. The normal verifier watches the addressed
/// slot; a single aggregate reconciliation handles slot reordering only when
/// that cheap path times out.
/// </summary>
public sealed class DalamudRetainerItemRetrieval
{
    private const string InputNumericAddon = "InputNumeric";
    private const string RetainerItemCommandSignature = "48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 57 48 83 EC 30 48 8B 5C 24 ?? 41 8B F0";
    private const string ApprovedGameVersion = "2026.08.05.0000.0000";
    private const string PatchContractId = "franthropy.retainer-item-command";
    private static readonly IReadOnlyList<InventoryType> PlayerOrdinaryItemContainers =
    [
        InventoryType.Inventory1,
        InventoryType.Inventory2,
        InventoryType.Inventory3,
        InventoryType.Inventory4,
    ];

    private readonly ISigScanner sigScanner;
    private readonly IGameGui gameGui;
    private readonly IFramework framework;
    private readonly IPluginLog log;
    private readonly IGameInventory? gameInventory;
    private RetainerItemCommandDelegate? retainerItemCommand;

    public DalamudRetainerItemRetrieval(
        ISigScanner sigScanner,
        IGameGui gameGui,
        IFramework framework,
        IPluginLog log,
        IGameInventory? gameInventory = null)
    {
        this.sigScanner = sigScanner;
        this.gameGui = gameGui;
        this.framework = framework;
        this.log = log;
        this.gameInventory = gameInventory;
    }

    public async Task<RetainerRetrievalResult> RetrieveAsync(
        DalamudInventoryStack stack,
        int requestedQuantity,
        CancellationToken cancellationToken = default) =>
        await RetrieveAsync(stack, requestedQuantity, null, cancellationToken).ConfigureAwait(false);

    public async Task<RetainerRetrievalResult> RetrieveAsync(
        DalamudInventoryStack stack,
        int requestedQuantity,
        int retainerVariantQuantityBefore,
        CancellationToken cancellationToken = default) =>
        await RetrieveAsync(stack, requestedQuantity, (int?)retainerVariantQuantityBefore, cancellationToken).ConfigureAwait(false);

    private async Task<RetainerRetrievalResult> RetrieveAsync(
        DalamudInventoryStack stack,
        int requestedQuantity,
        int? retainerVariantQuantityBefore,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Subscribe before submitting the native command. Dalamud reports the
        // source and destination changes as immutable snapshots, which lets us
        // prove the transfer even when the game immediately repopulates the slot.
        using var mutation = gameInventory is null
            ? null
            : new RetainerRetrievalMutationTracker(gameInventory, stack.ItemId, stack.IsHighQuality);
        var pending = await framework.RunOnTick(
            () => BeginRetrieval(stack, requestedQuantity, retainerVariantQuantityBefore),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!pending.Success)
            return new(false, 0, pending.Code, pending.Message);

        for (var attempt = 0; attempt < 30; attempt++)
        {
            var immediate = await framework.RunOnTick(
                () => VerifyCompleted(stack, pending),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (immediate.Success)
                return immediate;

            if (mutation?.Matches(pending.Requested) == true)
                return MutationVerified(stack.ItemId, pending.Requested, mutation);

            if (pending.NeedsQuantityInput)
            {
                var submitted = await framework.RunOnTick(
                    () => SubmitQuantity(stack.ItemId, pending.Requested),
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                if (submitted.Success)
                {
                    return await WaitForCompletionAsync(
                        stack,
                        pending with { Requested = submitted.Transferred },
                        mutation,
                        cancellationToken).ConfigureAwait(false);
                }
            }

            await framework.DelayTicks(1, cancellationToken).ConfigureAwait(false);
        }

        return await ReconcileAfterTimeoutAsync(stack, pending, mutation, cancellationToken).ConfigureAwait(false);
    }

    private async Task<RetainerRetrievalResult> WaitForCompletionAsync(
        DalamudInventoryStack stack,
        PendingRetainerRetrieval pending,
        RetainerRetrievalMutationTracker? mutation,
        CancellationToken cancellationToken)
    {
        RetainerRetrievalResult last = new(false, 0, "TransferPending", $"Retrieval did not complete for item {stack.ItemId}.");
        for (var attempt = 0; attempt < 60; attempt++)
        {
            last = await framework.RunOnTick(
                () => VerifyCompleted(stack, pending),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (last.Success)
                return last;

            if (mutation?.Matches(pending.Requested) == true)
                return MutationVerified(stack.ItemId, pending.Requested, mutation);

            await framework.DelayTicks(1, cancellationToken).ConfigureAwait(false);
        }

        return await ReconcileAfterTimeoutAsync(stack, pending, mutation, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Validates the selected source, captures both verification baselines, and
    /// submits exactly one native retrieval command.
    /// </summary>
    private unsafe PendingRetainerRetrieval BeginRetrieval(
        DalamudInventoryStack stack,
        int requestedQuantity,
        int? knownRetainerVariantQuantity)
    {
        var compatibility = GamePatchCompatibilityGate.Evaluate(PatchContractId, ApprovedGameVersion);
        if (!compatibility.IsApproved)
            return PendingRetainerRetrieval.Fail(GamePatchCompatibility.FailureCode, compatibility.Message);

        var supported = stack.Container == InventoryType.RetainerCrystals ||
                        DalamudRetainerInventory.OrdinaryItemContainers.Contains(stack.Container);
        if (!supported || requestedQuantity <= 0)
        {
            return PendingRetainerRetrieval.Fail(
                "InvalidRequest",
                $"Invalid retrieval request for item {stack.ItemId}: {requestedQuantity} from {stack.Container}.");
        }

        var inventoryManager = InventoryManager.Instance();
        if (inventoryManager == null)
            return PendingRetainerRetrieval.Fail("InventoryUnavailable", "Inventory manager is unavailable.");

        var container = inventoryManager->GetInventoryContainer(stack.Container);
        if (container == null || !container->IsLoaded)
            return PendingRetainerRetrieval.Fail("RetainerInventoryUnavailable", "Retainer source inventory is unavailable.");

        var slot = container->GetInventorySlot(stack.SlotIndex);
        var slotIsHighQuality = slot != null && slot->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality);
        if (slot == null ||
            slot->ItemId != stack.ItemId ||
            slot->Quantity != stack.Quantity ||
            slotIsHighQuality != stack.IsHighQuality)
        {
            return PendingRetainerRetrieval.Fail(
                "SourceSlotChanged",
                $"Expected {stack.Quantity}x item {stack.ItemId} was not found in {stack.Container} slot {stack.SlotIndex}.");
        }

        var transfer = Math.Min(requestedQuantity, stack.Quantity);
        var selection = RetainerRetrievalCommandPolicy.Select(
            stack.Container == InventoryType.RetainerCrystals,
            stack.Quantity,
            transfer);
        var playerBefore = CountPlayer(stack);
        // Quartermaster already owns this total from its route scan. Other consumers
        // retain compatibility by paying for one baseline scan before mutation.
        int retainerBefore;
        if (knownRetainerVariantQuantity is { } knownQuantity)
        {
            if (knownQuantity < stack.Quantity)
            {
                return PendingRetainerRetrieval.Fail(
                    "InvalidRetainerBaseline",
                    $"The known item total {knownQuantity} is smaller than source stack quantity {stack.Quantity}.");
            }

            retainerBefore = knownQuantity;
        }
        else if (!TryCountRetainer(stack, out retainerBefore))
        {
            return PendingRetainerRetrieval.Fail(
                "RetainerInventoryUnavailable",
                "Every retainer inventory container must be loaded before retrieval can be verified.");
        }
        var retainerAgent = AgentModule.Instance()->GetAgentByInternalId(AgentId.Retainer);
        if (retainerAgent == null || !retainerAgent->IsAgentActive())
            return PendingRetainerRetrieval.Fail("RetainerAgentUnavailable", "Retainer agent is unavailable.");

        try
        {
            retainerItemCommand ??= Marshal.GetDelegateForFunctionPointer<RetainerItemCommandDelegate>(
                sigScanner.ScanText(RetainerItemCommandSignature));
            retainerItemCommand(
                (nint)retainerAgent + 40,
                (uint)stack.SlotIndex,
                stack.Container,
                0,
                selection.Command);
        }
        catch (Exception ex)
        {
            log.Error(ex, "Unable to invoke the retainer retrieval command.");
            return PendingRetainerRetrieval.Fail(
                "CommandUnavailable",
                $"Retainer retrieval command is unavailable. {ex.Message}");
        }

        return new(
            true,
            transfer,
            playerBefore,
            retainerBefore,
            selection.NeedsQuantityInput,
            "CommandSubmitted",
            $"Submitted retrieval command for item {stack.ItemId}.");
    }

    private unsafe RetainerRetrievalResult SubmitQuantity(uint itemId, int requested)
    {
        var numeric = gameGui.GetAddonByName<AtkUnitBase>(InputNumericAddon, 1);
        if (numeric == null || !numeric->IsReady || !numeric->IsVisible || numeric->AtkValuesCount <= 3)
            return new(false, 0, "QuantityInputPending", $"Numeric quantity popup did not open for item {itemId}.");

        var maximum = checked((int)numeric->AtkValues[3].UInt);
        if (maximum <= 0)
            return new(false, 0, "QuantityUnavailable", $"Retainer reported no retrievable quantity for item {itemId}.");

        var submitted = Math.Clamp(requested, 1, maximum);
        numeric->FireCallbackInt(submitted);
        return new(true, submitted, "QuantitySubmitted", $"Submitted {submitted}x item {itemId} for retrieval.");
    }

    /// <summary>
    /// Polls the inexpensive common-path evidence: the addressed source slot and
    /// the player's total for the same item variant.
    /// </summary>
    private static unsafe RetainerRetrievalResult VerifyCompleted(
        DalamudInventoryStack original,
        PendingRetainerRetrieval pending)
    {
        var inventoryManager = InventoryManager.Instance();
        if (inventoryManager == null)
            return new(false, 0, "ContainerUnavailable", "Inventory manager became unavailable.");

        var container = inventoryManager->GetInventoryContainer(original.Container);
        if (container == null || !container->IsLoaded)
            return new(false, 0, "ContainerUnavailable", "Retainer source container became unavailable.");

        var slot = container->GetInventorySlot(original.SlotIndex);
        if (slot == null)
            return new(false, 0, "SlotUnavailable", "Retainer source slot became unavailable.");

        var playerAfter = CountPlayer(original);
        if (RetainerRetrievalObservation.Matches(
                original.ItemId,
                original.Quantity,
                pending.Requested,
                slot->ItemId,
                slot->Quantity,
                pending.PlayerQuantityBefore,
                playerAfter))
        {
            return new(
                true,
                pending.Requested,
                "TransferVerified",
                $"Retrieved {pending.Requested}x item {original.ItemId}; player {pending.PlayerQuantityBefore}->{playerAfter}.");
        }

        return new(false, 0, "TransferPending", "Waiting for matching retainer-slot and player-inventory deltas.");
    }

    /// <summary>
    /// Performs one bounded recovery read after exact-slot polling has failed. This
    /// is intentionally outside the polling loop: aggregate retainer scans are
    /// reserved for the rare case where the game moved the item but changed which
    /// physical slot represents the remaining stack.
    /// </summary>
    private async Task<RetainerRetrievalResult> ReconcileAfterTimeoutAsync(
        DalamudInventoryStack original,
        PendingRetainerRetrieval pending,
        RetainerRetrievalMutationTracker? mutation,
        CancellationToken cancellationToken) =>
        mutation?.Matches(pending.Requested) == true
            ? MutationVerified(original.ItemId, pending.Requested, mutation)
            : await framework.RunOnTick(
                () => VerifyAggregateCompleted(original, pending),
                cancellationToken: cancellationToken).ConfigureAwait(false);

    private static RetainerRetrievalResult MutationVerified(
        uint itemId,
        int requested,
        RetainerRetrievalMutationTracker mutation)
    {
        var evidence = mutation.Snapshot();
        return new(
            true,
            requested,
            "TransferVerified",
            $"Retrieved {requested}x item {itemId}; command-scoped inventory changes confirmed retainer {evidence.RetainerDelta:+#;-#;0}, player {evidence.PlayerDelta:+#;-#;0}.");
    }

    /// <summary>
    /// Accepts the timeout recovery only when the retainer and player report equal,
    /// opposite, exact deltas for the requested quantity.
    /// </summary>
    private static RetainerRetrievalResult VerifyAggregateCompleted(
        DalamudInventoryStack original,
        PendingRetainerRetrieval pending)
    {
        var playerAfter = CountPlayer(original);
        if (!TryCountRetainer(original, out var retainerAfter))
        {
            return new(
                false,
                0,
                "RetrievalNotObserved",
                $"Retrieval of item {original.ItemId} could not be reconciled because retainer inventory became unavailable.");
        }
        if (RetainerRetrievalObservation.MatchesAggregate(
                pending.Requested,
                pending.RetainerQuantityBefore,
                retainerAfter,
                pending.PlayerQuantityBefore,
                playerAfter))
        {
            return new(
                true,
                pending.Requested,
                "TransferVerified",
                $"Retrieved {pending.Requested}x item {original.ItemId}; aggregate inventories confirmed a reordered source slot.");
        }

        return new(
            false,
            0,
            "RetrievalNotObserved",
            $"Retrieval of item {original.ItemId} could not be proven after one aggregate reconciliation: retainer {pending.RetainerQuantityBefore}->{retainerAfter}, player {pending.PlayerQuantityBefore}->{playerAfter}, expected {pending.Requested}.");
    }

    /// <summary>Counts the affected item variant across the player's loaded bags.</summary>
    private static int CountPlayer(DalamudInventoryStack stack) =>
        stack.Container == InventoryType.RetainerCrystals
            ? DalamudInventoryStackScanner.CountLoadedItem(InventoryType.Crystals, stack.ItemId)
            : PlayerOrdinaryItemContainers.Sum(
                type => DalamudInventoryStackScanner.CountLoadedItem(type, stack.ItemId, stack.IsHighQuality));

    /// <summary>
    /// Counts only the affected item variant across the currently open retainer.
    /// Callers should supply their existing route-scan total for the before value;
    /// this reader is retained for the single timeout reconciliation and compatible
    /// consumers that do not already own such a snapshot.
    /// </summary>
    private static unsafe bool TryCountRetainer(DalamudInventoryStack stack, out int quantity)
    {
        quantity = 0;
        var inventoryManager = InventoryManager.Instance();
        if (inventoryManager == null)
            return false;

        IReadOnlyList<InventoryType> containers = stack.Container == InventoryType.RetainerCrystals
            ? [InventoryType.RetainerCrystals]
            : DalamudRetainerInventory.OrdinaryItemContainers;
        foreach (var inventoryType in containers)
        {
            var container = inventoryManager->GetInventoryContainer(inventoryType);
            if (container == null || !container->IsLoaded)
                return false;

            quantity += DalamudInventoryStackScanner.CountLoadedItem(
                inventoryType,
                stack.ItemId,
                stack.Container == InventoryType.RetainerCrystals ? null : stack.IsHighQuality);
        }

        return true;
    }

    private delegate void RetainerItemCommandDelegate(
        nint AgentRetainerItemCommandModule,
        uint Slot,
        InventoryType InventoryType,
        uint A4,
        RetainerRetrievalCommand Command);

    private sealed record PendingRetainerRetrieval(
        bool Success,
        int Requested,
        int PlayerQuantityBefore,
        int RetainerQuantityBefore,
        bool NeedsQuantityInput,
        string Code,
        string Message)
    {
        public static PendingRetainerRetrieval Fail(string code, string message) =>
            new(false, 0, 0, 0, false, code, message);
    }
}

/// <summary>
/// Accumulates the net item movement emitted while one retrieval command is in
/// flight. Internal rearrangement produces equal additions and removals, so it
/// cannot masquerade as stock crossing the retainer boundary.
/// </summary>
internal sealed class RetainerRetrievalMutationAccumulator
{
    private int playerDelta;
    private int retainerDelta;

    public void RecordPlayer(int quantityDelta) => playerDelta += quantityDelta;
    public void RecordRetainer(int quantityDelta) => retainerDelta += quantityDelta;
    public RetainerRetrievalMutationEvidence Snapshot() => new(retainerDelta, playerDelta);
    public bool Matches(int requested) =>
        RetainerRetrievalObservation.MatchesMutation(requested, retainerDelta, playerDelta);
}

internal readonly record struct RetainerRetrievalMutationEvidence(
    int RetainerDelta,
    int PlayerDelta);

/// <summary>
/// Converts Dalamud's per-slot inventory events into one command-scoped net
/// movement. The subscription is deliberately short-lived and is disposed as
/// soon as the retrieval reaches a terminal result.
/// </summary>
internal sealed class RetainerRetrievalMutationTracker : IDisposable
{
    private static readonly IReadOnlySet<GameInventoryType> PlayerContainers = new HashSet<GameInventoryType>
    {
        GameInventoryType.Inventory1,
        GameInventoryType.Inventory2,
        GameInventoryType.Inventory3,
        GameInventoryType.Inventory4,
    };
    private static readonly IReadOnlySet<GameInventoryType> RetainerContainers = new HashSet<GameInventoryType>
    {
        GameInventoryType.RetainerPage1,
        GameInventoryType.RetainerPage2,
        GameInventoryType.RetainerPage3,
        GameInventoryType.RetainerPage4,
        GameInventoryType.RetainerPage5,
        GameInventoryType.RetainerPage6,
        GameInventoryType.RetainerPage7,
        GameInventoryType.RetainerCrystals,
    };

    private readonly IGameInventory inventory;
    private readonly uint itemId;
    private readonly bool isHighQuality;
    private readonly object gate = new();
    private readonly RetainerRetrievalMutationAccumulator accumulator = new();

    public RetainerRetrievalMutationTracker(IGameInventory inventory, uint itemId, bool isHighQuality)
    {
        this.inventory = inventory;
        this.itemId = itemId;
        this.isHighQuality = isHighQuality;
        inventory.InventoryChanged += OnInventoryChanged;
    }

    public bool Matches(int requested)
    {
        lock (gate)
            return accumulator.Matches(requested);
    }

    public RetainerRetrievalMutationEvidence Snapshot()
    {
        lock (gate)
            return accumulator.Snapshot();
    }

    public void Dispose() => inventory.InventoryChanged -= OnInventoryChanged;

    private void OnInventoryChanged(IReadOnlyCollection<InventoryEventArgs> events)
    {
        lock (gate)
        {
            foreach (var change in events.SelectMany(Flatten))
            {
                var quantityDelta = QuantityDelta(change);
                if (quantityDelta == 0)
                    continue;
                if (PlayerContainers.Contains(change.Item.ContainerType))
                    accumulator.RecordPlayer(quantityDelta);
                else if (RetainerContainers.Contains(change.Item.ContainerType))
                    accumulator.RecordRetainer(quantityDelta);
            }
        }
    }

    private int QuantityDelta(InventoryEventArgs change)
    {
        if (change is InventoryItemChangedArgs changed)
            return TrackedQuantity(change.Item) - TrackedQuantity(changed.OldItemState);
        if (change.Type == GameInventoryEvent.Added)
            return TrackedQuantity(change.Item);
        if (change.Type == GameInventoryEvent.Removed)
            return -TrackedQuantity(change.Item);
        return 0;
    }

    private int TrackedQuantity(GameInventoryItem item) =>
        item.BaseItemId == itemId && item.IsHq == isHighQuality
            ? checked((int)item.Quantity)
            : 0;

    private static IEnumerable<InventoryEventArgs> Flatten(InventoryEventArgs change)
    {
        if (change is InventoryComplexEventArgs complex)
        {
            foreach (var source in Flatten(complex.SourceEvent))
                yield return source;
            foreach (var target in Flatten(complex.TargetEvent))
                yield return target;
            yield break;
        }
        yield return change;
    }
}
