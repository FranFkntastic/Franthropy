using Dalamud.Plugin.Services;
using ECommons.Automation.UIInput;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Franthropy.Dalamud.Automation.Inventory;

public sealed record ItemQualityLoweringRequirement(
    uint ItemId,
    string ItemName,
    int RequiredNormalQualityQuantity);

public enum ItemQualityLoweringAutomationState
{
    Idle,
    Preparing,
    WaitingForConfirmation,
    WaitingForInventory,
    Completed,
    Failed,
    Stopped,
}

public sealed record ItemQualityLoweringAutomationSnapshot(
    ItemQualityLoweringAutomationState State,
    string Message,
    uint? ActiveItemId,
    int RemainingHighQualityUnits,
    bool IsActive);

public interface IItemQualityLoweringAutomation
{
    ItemQualityLoweringAutomationSnapshot Snapshot { get; }
    ItemQualityLoweringAutomationSnapshot Begin(IReadOnlyList<ItemQualityLoweringRequirement> requested);
    ItemQualityLoweringAutomationSnapshot Advance(Func<bool> mutationStillAuthorized);
    void Stop(string message = "Quality lowering stopped.");
}

public sealed record ItemQualityLoweringPlanStep(
    bool Success,
    bool Completed,
    string Message,
    DalamudInventoryStack? Stack,
    int RemainingHighQualityUnits);

/// <summary>
/// Resolves the next exact HQ stack that must be lowered so every requirement can
/// be fulfilled from normal-quality inventory. Product code owns the requirements;
/// this planner owns only quality normalization.
/// </summary>
public static class ItemQualityLoweringPlanner
{
    public static ItemQualityLoweringPlanStep ResolveNext(
        IReadOnlyList<ItemQualityLoweringRequirement> requirements,
        IReadOnlyList<DalamudInventoryStack> stacks)
    {
        ArgumentNullException.ThrowIfNull(requirements);
        ArgumentNullException.ThrowIfNull(stacks);

        foreach (var requirement in requirements)
        {
            if (requirement.ItemId == 0 || requirement.RequiredNormalQualityQuantity < 0)
                return new(false, false, $"{requirement.ItemName} has an invalid quality-lowering requirement.", null, 0);

            var matching = stacks
                .Where(stack => stack.ItemId == requirement.ItemId && stack.Quantity > 0)
                .ToArray();
            var normalQuantity = matching
                .Where(stack => !stack.IsHighQuality)
                .Sum(stack => stack.Quantity);
            var remaining = Math.Max(0, requirement.RequiredNormalQualityQuantity - normalQuantity);
            if (remaining == 0)
                continue;

            var highQuality = matching
                .Where(stack => stack.IsHighQuality)
                .OrderBy(stack => stack.Quantity)
                .ThenBy(stack => stack.Container)
                .ThenBy(stack => stack.SlotIndex)
                .ToArray();
            var highQualityQuantity = highQuality.Sum(stack => stack.Quantity);
            if (highQualityQuantity < remaining)
            {
                return new(
                    false,
                    false,
                    $"{requirement.ItemName} needs {requirement.RequiredNormalQualityQuantity:N0} normal-quality units, " +
                    $"but only {normalQuantity + highQualityQuantity:N0} combined units are available.",
                    null,
                    remaining);
            }

            return new(
                true,
                false,
                $"Lowering {highQuality[0].Quantity:N0} {requirement.ItemName} from HQ to NQ.",
                highQuality[0],
                remaining);
        }

        return new(true, true, "Required inventory is available at normal quality.", null, 0);
    }
}

/// <summary>
/// Lowers exact HQ inventory stacks through the game's native quality-lowering flow,
/// owns its confirmation dialog, and requires exact aggregate inventory evidence
/// before advancing to another stack.
/// </summary>
public sealed class DalamudItemQualityLoweringAutomation : IItemQualityLoweringAutomation
{
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(20);
    private const uint LowerItemQualityPermission = 135;
    private const int LowerQualityDialogType = 2;
    private const string SelectYesNoAddon = "SelectYesno";

    private readonly IGameGui gameGui;
    private readonly IReadOnlyList<InventoryType> inventoryTypes;
    private readonly Func<DateTimeOffset> clock;
    private IReadOnlyList<ItemQualityLoweringRequirement> requirements = [];
    private DalamudInventoryStack? activeStack;
    private int highQualityBefore;
    private int normalQualityBefore;
    private DateTimeOffset deadline;

    public DalamudItemQualityLoweringAutomation(
        IGameGui gameGui,
        IReadOnlyList<InventoryType> inventoryTypes)
        : this(gameGui, inventoryTypes, () => DateTimeOffset.UtcNow)
    {
    }

    internal DalamudItemQualityLoweringAutomation(
        IGameGui gameGui,
        IReadOnlyList<InventoryType> inventoryTypes,
        Func<DateTimeOffset> clock)
    {
        this.gameGui = gameGui ?? throw new ArgumentNullException(nameof(gameGui));
        this.inventoryTypes = inventoryTypes?.Distinct().ToArray() ??
                              throw new ArgumentNullException(nameof(inventoryTypes));
        if (this.inventoryTypes.Count == 0)
            throw new ArgumentException("At least one inventory container is required.", nameof(inventoryTypes));
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public ItemQualityLoweringAutomationSnapshot Snapshot { get; private set; } =
        new(ItemQualityLoweringAutomationState.Idle, "Quality lowering is idle.", null, 0, false);

    public ItemQualityLoweringAutomationSnapshot Begin(
        IReadOnlyList<ItemQualityLoweringRequirement> requested)
    {
        ArgumentNullException.ThrowIfNull(requested);
        if (Snapshot.IsActive)
            return Fail("Quality lowering is already active.");

        requirements = requested
            .Where(requirement => requirement.RequiredNormalQualityQuantity > 0)
            .GroupBy(requirement => requirement.ItemId)
            .Select(group => new ItemQualityLoweringRequirement(
                group.Key,
                group.First().ItemName,
                checked(group.Sum(requirement => requirement.RequiredNormalQualityQuantity))))
            .ToArray();
        activeStack = null;
        deadline = clock() + OperationTimeout;
        Snapshot = new(
            ItemQualityLoweringAutomationState.Preparing,
            "Checking live HQ and NQ inventory.",
            null,
            0,
            true);
        return Snapshot;
    }

    public unsafe ItemQualityLoweringAutomationSnapshot Advance(Func<bool> mutationStillAuthorized)
    {
        ArgumentNullException.ThrowIfNull(mutationStillAuthorized);
        if (!Snapshot.IsActive)
            return Snapshot;
        if (!mutationStillAuthorized())
            return Fail("Quality-lowering authorization changed before the inventory mutation completed.");
        if (clock() > deadline)
            return Fail($"Timed out while {DescribeState(Snapshot.State)}.");

        try
        {
            return Snapshot.State switch
            {
                ItemQualityLoweringAutomationState.Preparing => PrepareNext(),
                ItemQualityLoweringAutomationState.WaitingForConfirmation => ConfirmActiveStack(),
                ItemQualityLoweringAutomationState.WaitingForInventory => VerifyActiveStack(),
                _ => Snapshot,
            };
        }
        catch (Exception exception)
        {
            return Fail($"Quality lowering failed: {exception.Message}");
        }
    }

    public unsafe void Stop(string message = "Quality lowering stopped.")
    {
        if (!Snapshot.IsActive)
            return;

        var yesNo = gameGui.GetAddonByName<AddonSelectYesno>(SelectYesNoAddon, 1);
        var agent = AgentInventoryContext.Instance();
        if (activeStack is not null &&
            Snapshot.State == ItemQualityLoweringAutomationState.WaitingForConfirmation &&
            IsExpectedConfirmation(yesNo, agent, activeStack) &&
            yesNo->NoButton != null &&
            yesNo->NoButton->IsEnabled)
        {
            yesNo->NoButton->ClickAddonButton(&yesNo->AtkUnitBase);
        }

        activeStack = null;
        deadline = default;
        Snapshot = new(ItemQualityLoweringAutomationState.Stopped, message, null, 0, false);
    }

    private unsafe ItemQualityLoweringAutomationSnapshot PrepareNext()
    {
        if (IsVisible(SelectYesNoAddon))
            return Pending(ItemQualityLoweringAutomationState.Preparing, "Waiting for an unrelated confirmation to close.");

        var stacks = Scan();
        var step = ItemQualityLoweringPlanner.ResolveNext(requirements, stacks);
        if (!step.Success)
            return Fail(step.Message);
        if (step.Completed)
        {
            activeStack = null;
            deadline = default;
            Snapshot = new(ItemQualityLoweringAutomationState.Completed, step.Message, null, 0, false);
            return Snapshot;
        }

        var rapture = RaptureAtkModule.Instance();
        var conditions = Conditions.Instance();
        var agent = AgentInventoryContext.Instance();
        if (rapture == null || conditions == null || agent == null)
            return Pending(ItemQualityLoweringAutomationState.Preparing, "Waiting for inventory automation services.");
        if (rapture->AgentUpdateFlag.HasFlag(RaptureAtkModule.AgentUpdateFlags.InventoryUpdate))
            return Pending(ItemQualityLoweringAutomationState.Preparing, "Waiting for the current inventory update.");
        if (!conditions->HasPermission(LowerItemQualityPermission))
            return Pending(ItemQualityLoweringAutomationState.Preparing, "Waiting until item quality can be lowered.");

        activeStack = step.Stack ?? throw new InvalidOperationException("The lowering plan did not select an HQ stack.");
        var slot = ResolveExactSlot(activeStack);
        if (slot == null)
            return Pending(ItemQualityLoweringAutomationState.Preparing, "The selected HQ stack changed; replanning.");

        (normalQualityBefore, highQualityBefore) = CountQuality(activeStack.ItemId, stacks);
        agent->LowerItemQuality(slot, activeStack.Container, activeStack.SlotIndex, 0);
        deadline = clock() + OperationTimeout;
        Snapshot = new(
            ItemQualityLoweringAutomationState.WaitingForConfirmation,
            step.Message,
            activeStack.ItemId,
            step.RemainingHighQualityUnits,
            true);
        return Snapshot;
    }

    private unsafe ItemQualityLoweringAutomationSnapshot ConfirmActiveStack()
    {
        var target = activeStack ?? throw new InvalidOperationException("The active HQ stack is unavailable.");
        var yesNo = gameGui.GetAddonByName<AddonSelectYesno>(SelectYesNoAddon, 1);
        if (yesNo == null || !yesNo->AtkUnitBase.IsReady || !yesNo->AtkUnitBase.IsVisible)
            return Snapshot;

        var agent = AgentInventoryContext.Instance();
        if (!IsExpectedConfirmation(yesNo, agent, target))
            return Fail("An unexpected confirmation appeared while lowering item quality.");
        if (ResolveExactSlot(target) == null)
            return Fail("The approved HQ stack changed before quality lowering was confirmed.");
        if (yesNo->YesButton == null || !yesNo->YesButton->IsEnabled)
            return Snapshot;

        yesNo->YesButton->ClickAddonButton(&yesNo->AtkUnitBase);
        deadline = clock() + OperationTimeout;
        Snapshot = Snapshot with
        {
            State = ItemQualityLoweringAutomationState.WaitingForInventory,
            Message = $"Confirmed HQ-to-NQ conversion for item {target.ItemId}; verifying inventory evidence.",
        };
        return Snapshot;
    }

    private ItemQualityLoweringAutomationSnapshot VerifyActiveStack()
    {
        var target = activeStack ?? throw new InvalidOperationException("The active HQ stack is unavailable.");
        var stacks = Scan();
        var (normalAfter, highAfter) = CountQuality(target.ItemId, stacks);
        if (normalAfter == normalQualityBefore + target.Quantity &&
            highAfter == highQualityBefore - target.Quantity)
        {
            activeStack = null;
            Snapshot = new(
                ItemQualityLoweringAutomationState.Preparing,
                $"Verified {target.Quantity:N0} units lowered from HQ to NQ.",
                null,
                0,
                true);
            deadline = clock() + OperationTimeout;
        }

        return Snapshot;
    }

    private IReadOnlyList<DalamudInventoryStack> Scan() =>
        DalamudInventoryStackScanner.ScanLoadedStacks(
            inventoryTypes,
            requirements.Select(requirement => requirement.ItemId).ToHashSet());

    private static (int Normal, int High) CountQuality(
        uint itemId,
        IReadOnlyList<DalamudInventoryStack> stacks) =>
        (
            stacks.Where(stack => stack.ItemId == itemId && !stack.IsHighQuality).Sum(stack => stack.Quantity),
            stacks.Where(stack => stack.ItemId == itemId && stack.IsHighQuality).Sum(stack => stack.Quantity)
        );

    private static unsafe InventoryItem* ResolveExactSlot(DalamudInventoryStack expected)
    {
        var inventoryManager = InventoryManager.Instance();
        var container = inventoryManager == null
            ? null
            : inventoryManager->GetInventoryContainer(expected.Container);
        if (container == null ||
            !container->IsLoaded ||
            expected.SlotIndex < 0 ||
            expected.SlotIndex >= container->Size)
        {
            return null;
        }

        var slot = container->GetInventorySlot(expected.SlotIndex);
        return slot != null &&
               slot->ItemId == expected.ItemId &&
               slot->Quantity == expected.Quantity &&
               slot->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality)
            ? slot
            : null;
    }

    private static unsafe bool IsExpectedConfirmation(
        AddonSelectYesno* yesNo,
        AgentInventoryContext* agent,
        DalamudInventoryStack target) =>
        yesNo != null &&
        yesNo->AtkUnitBase.IsReady &&
        yesNo->AtkUnitBase.IsVisible &&
        agent != null &&
        agent->DialogType == LowerQualityDialogType &&
        agent->TargetInventoryId == target.Container &&
        agent->TargetInventorySlotId == target.SlotIndex;

    private unsafe bool IsVisible(string addonName)
    {
        var addon = gameGui.GetAddonByName<AtkUnitBase>(addonName, 1);
        return addon != null && addon->IsReady && addon->IsVisible;
    }

    private ItemQualityLoweringAutomationSnapshot Pending(
        ItemQualityLoweringAutomationState state,
        string message)
    {
        Snapshot = Snapshot with { State = state, Message = message };
        return Snapshot;
    }

    private ItemQualityLoweringAutomationSnapshot Fail(string message)
    {
        activeStack = null;
        deadline = default;
        Snapshot = new(ItemQualityLoweringAutomationState.Failed, message, null, 0, false);
        return Snapshot;
    }

    private static string DescribeState(ItemQualityLoweringAutomationState state) => state switch
    {
        ItemQualityLoweringAutomationState.Preparing => "preparing the next HQ stack",
        ItemQualityLoweringAutomationState.WaitingForConfirmation => "waiting for the quality-lowering confirmation",
        ItemQualityLoweringAutomationState.WaitingForInventory => "verifying the HQ-to-NQ inventory transition",
        _ => state.ToString(),
    };
}
