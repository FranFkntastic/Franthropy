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

            var next = highQuality
                .Where(stack => stack.Quantity >= remaining)
                .OrderBy(stack => stack.Quantity)
                .ThenBy(stack => stack.Container)
                .ThenBy(stack => stack.SlotIndex)
                .FirstOrDefault()
                ?? highQuality
                    .OrderByDescending(stack => stack.Quantity)
                    .ThenBy(stack => stack.Container)
                    .ThenBy(stack => stack.SlotIndex)
                    .First();
            return new(
                true,
                false,
                $"Lowering {next.Quantity:N0} {requirement.ItemName} from HQ to NQ.",
                next,
                remaining);
        }

        return new(true, true, "Required inventory is available at normal quality.", null, 0);
    }

    /// <summary>
    /// Merges requirements per item exactly as the automation does before execution.
    /// </summary>
    public static IReadOnlyList<ItemQualityLoweringRequirement> Normalize(
        IReadOnlyList<ItemQualityLoweringRequirement> requested)
    {
        ArgumentNullException.ThrowIfNull(requested);
        return requested
            .Where(requirement => requirement.RequiredNormalQualityQuantity > 0)
            .GroupBy(requirement => requirement.ItemId)
            .Select(group => new ItemQualityLoweringRequirement(
                group.Key,
                group.First().ItemName,
                checked(group.Sum(requirement => requirement.RequiredNormalQualityQuantity))))
            .ToArray();
    }

    /// <summary>
    /// Dry-runs <see cref="ResolveNext"/> against a copy of the given stacks and returns every
    /// HQ stack the automation would lower, in order. The game lowers whole stacks, so a step
    /// can convert more units than the requirement needs; that excess is reported explicitly.
    /// </summary>
    public static ItemQualityLoweringPreview Preview(
        IReadOnlyList<ItemQualityLoweringRequirement> requested,
        IReadOnlyList<DalamudInventoryStack> stacks)
    {
        ArgumentNullException.ThrowIfNull(stacks);
        var requirements = Normalize(requested);
        var simulated = stacks.ToList();
        var steps = new List<ItemQualityLoweringPreviewStep>();

        // Each step flips one HQ stack to NQ, so the plan converges within stacks.Count steps.
        for (var guard = 0; guard <= stacks.Count; guard++)
        {
            var step = ResolveNext(requirements, simulated);
            if (!step.Success)
                return new(false, step.Message, steps);
            if (step.Completed)
            {
                return new(
                    true,
                    steps.Count == 0
                        ? "No HQ items need to be lowered."
                        : $"{steps.Sum(s => s.ConvertedUnits):N0} HQ units across {steps.Count:N0} stack(s) will be lowered to NQ.",
                    steps);
            }

            var stack = step.Stack!;
            var name = requirements.First(requirement => requirement.ItemId == stack.ItemId).ItemName;
            steps.Add(new(
                stack.ItemId,
                name,
                stack.Container,
                stack.SlotIndex,
                stack.Quantity,
                Math.Min(stack.Quantity, step.RemainingHighQualityUnits)));
            simulated[simulated.IndexOf(stack)] = stack with { IsHighQuality = false };
        }

        return new(false, "The quality-lowering preview did not converge.", steps);
    }
}

public sealed record ItemQualityLoweringPreviewStep(
    uint ItemId,
    string ItemName,
    InventoryType Container,
    int SlotIndex,
    int ConvertedUnits,
    int RequiredUnits)
{
    /// <summary>Units converted beyond the requirement because the game lowers whole stacks.</summary>
    public int ExcessConvertedUnits => ConvertedUnits - RequiredUnits;
}

public sealed record ItemQualityLoweringPreview(
    bool Success,
    string Message,
    IReadOnlyList<ItemQualityLoweringPreviewStep> Steps)
{
    public bool RequiresLowering => Steps.Count > 0;
    public int ConvertedUnits => Steps.Sum(step => step.ConvertedUnits);
    public int ExcessConvertedUnits => Steps.Sum(step => step.ExcessConvertedUnits);
}

/// <summary>
/// Lowers exact HQ inventory stacks through the game's native quality-lowering flow,
/// owns its confirmation dialog, and requires exact aggregate inventory evidence
/// before advancing to another stack.
/// </summary>
/// <remarks>
/// When a log is supplied, every wait reason, native dispatch, post-dispatch observation, and
/// terminal outcome is written once per change, so a stalled run shows which gate held it.
/// </remarks>
public sealed class DalamudItemQualityLoweringAutomation : IItemQualityLoweringAutomation
{
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(20);
    private const uint LowerItemQualityPermission = 135;
    private const int LowerQualityDialogType = 2;
    private const string SelectYesNoAddon = "SelectYesno";
    private const string LogPrefix = "[QualityLowering]";

    private readonly IGameGui gameGui;
    private readonly IReadOnlyList<InventoryType> inventoryTypes;
    private readonly Func<DateTimeOffset> clock;
    private readonly IPluginLog? log;
    private readonly ICondition? condition;
    private IReadOnlyList<ItemQualityLoweringRequirement> requirements = [];
    private DalamudInventoryStack? activeStack;
    private string activeItemName = string.Empty;
    private int highQualityBefore;
    private int normalQualityBefore;
    private DateTimeOffset deadline;
    private DateTimeOffset dispatchedAt;
    private string? lastObservation;

    public DalamudItemQualityLoweringAutomation(
        IGameGui gameGui,
        IReadOnlyList<InventoryType> inventoryTypes,
        IPluginLog? log = null,
        ICondition? condition = null)
        : this(gameGui, inventoryTypes, () => DateTimeOffset.UtcNow, log, condition)
    {
    }

    internal DalamudItemQualityLoweringAutomation(
        IGameGui gameGui,
        IReadOnlyList<InventoryType> inventoryTypes,
        Func<DateTimeOffset> clock,
        IPluginLog? log = null,
        ICondition? condition = null)
    {
        this.gameGui = gameGui ?? throw new ArgumentNullException(nameof(gameGui));
        this.inventoryTypes = inventoryTypes?.Distinct().ToArray() ??
                              throw new ArgumentNullException(nameof(inventoryTypes));
        if (this.inventoryTypes.Count == 0)
            throw new ArgumentException("At least one inventory container is required.", nameof(inventoryTypes));
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        this.log = log;
        this.condition = condition;
    }

    public ItemQualityLoweringAutomationSnapshot Snapshot { get; private set; } =
        new(ItemQualityLoweringAutomationState.Idle, "Quality lowering is idle.", null, 0, false);

    public ItemQualityLoweringAutomationSnapshot Begin(
        IReadOnlyList<ItemQualityLoweringRequirement> requested)
    {
        ArgumentNullException.ThrowIfNull(requested);
        if (Snapshot.IsActive)
            return Fail("Quality lowering is already active.");

        requirements = ItemQualityLoweringPlanner.Normalize(requested);
        activeStack = null;
        activeItemName = string.Empty;
        lastObservation = null;
        deadline = clock() + OperationTimeout;
        Event(
            "Begin: requirements=[" +
            string.Join(", ", requirements.Select(r => $"{r.ItemId} {r.ItemName} x{r.RequiredNormalQualityQuantity}")) +
            $"] containers=[{string.Join(", ", inventoryTypes)}]");
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
            return Fail($"Timed out while {DescribeState(Snapshot.State)}. Last observation: {Snapshot.Message}");

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
            log?.Error(exception, $"{LogPrefix} Advance threw in state {Snapshot.State}.");
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
            Event("Stop declined the owned quality-lowering confirmation.");
        }

        Event($"Stopped in state {Snapshot.State}: {message} Last observation: {Snapshot.Message}");
        activeStack = null;
        deadline = default;
        Snapshot = new(ItemQualityLoweringAutomationState.Stopped, message, null, 0, false);
    }

    private unsafe ItemQualityLoweringAutomationSnapshot PrepareNext()
    {
        if (IsVisible(SelectYesNoAddon))
        {
            var prompt = DescribeYesNoPrompt(gameGui.GetAddonByName<AddonSelectYesno>(SelectYesNoAddon, 1));
            return Pending(
                ItemQualityLoweringAutomationState.Preparing,
                $"Waiting for an unrelated confirmation to close: \"{prompt}\".");
        }

        var stacks = Scan();
        var step = ItemQualityLoweringPlanner.ResolveNext(requirements, stacks);
        if (!step.Success)
            return Fail(step.Message);
        if (step.Completed)
        {
            activeStack = null;
            deadline = default;
            Event($"Completed: {step.Message}");
            Snapshot = new(ItemQualityLoweringAutomationState.Completed, step.Message, null, 0, false);
            return Snapshot;
        }

        var rapture = RaptureAtkModule.Instance();
        var conditions = Conditions.Instance();
        var agent = AgentInventoryContext.Instance();
        if (rapture == null || conditions == null || agent == null)
        {
            return Pending(
                ItemQualityLoweringAutomationState.Preparing,
                "Waiting for inventory automation services.",
                $"rapture={rapture != null} conditions={conditions != null} agent={agent != null}");
        }
        if (rapture->AgentUpdateFlag.HasFlag(RaptureAtkModule.AgentUpdateFlags.InventoryUpdate))
            return Pending(ItemQualityLoweringAutomationState.Preparing, "Waiting for the current inventory update.");
        if (!conditions->HasPermission(LowerItemQualityPermission))
        {
            return Pending(
                ItemQualityLoweringAutomationState.Preparing,
                "Waiting until the game allows lowering item quality.",
                $"permission {LowerItemQualityPermission} denied; active conditions=[{DescribeActiveConditions()}]");
        }

        var candidate = step.Stack ?? throw new InvalidOperationException("The lowering plan did not select an HQ stack.");
        var slot = ResolveExactSlot(candidate);
        if (slot == null)
        {
            return Pending(
                ItemQualityLoweringAutomationState.Preparing,
                "The selected HQ stack changed; replanning.",
                $"expected {Describe(candidate)}; live {DescribeLiveSlot(candidate)}");
        }

        activeStack = candidate;
        activeItemName = requirements.FirstOrDefault(r => r.ItemId == candidate.ItemId)?.ItemName ?? $"item {candidate.ItemId}";
        (normalQualityBefore, highQualityBefore) = CountQuality(candidate.ItemId, stacks);
        Event(
            $"Dispatching LowerItemQuality for {activeItemName} {Describe(candidate)}; " +
            $"before NQ={normalQualityBefore} HQ={highQualityBefore}; still needed={step.RemainingHighQualityUnits}; " +
            $"agent before: {DescribeAgent(agent)}");
        agent->LowerItemQuality(slot, candidate.Container, candidate.SlotIndex, 0);
        dispatchedAt = clock();
        Event(
            $"Dispatched. agent after: {DescribeAgent(agent)}; " +
            DescribeYesNo(gameGui.GetAddonByName<AddonSelectYesno>(SelectYesNoAddon, 1)));
        deadline = dispatchedAt + OperationTimeout;
        Snapshot = new(
            ItemQualityLoweringAutomationState.WaitingForConfirmation,
            step.Message,
            candidate.ItemId,
            step.RemainingHighQualityUnits,
            true);
        return Snapshot;
    }

    private unsafe ItemQualityLoweringAutomationSnapshot ConfirmActiveStack()
    {
        var target = activeStack ?? throw new InvalidOperationException("The active HQ stack is unavailable.");
        var yesNo = gameGui.GetAddonByName<AddonSelectYesno>(SelectYesNoAddon, 1);
        var agent = AgentInventoryContext.Instance();
        var elapsed = (clock() - dispatchedAt).TotalSeconds;
        if (yesNo == null || !yesNo->AtkUnitBase.IsReady || !yesNo->AtkUnitBase.IsVisible)
        {
            // Elapsed time is deliberately left out of the deduplicated detail so one line covers the wait.
            return Pending(
                ItemQualityLoweringAutomationState.WaitingForConfirmation,
                $"Asked the game to lower {target.Quantity:N0} {activeItemName}; waiting for its confirmation dialog.",
                $"{DescribeYesNo(yesNo)}; agent: {DescribeAgent(agent)}");
        }

        if (!IsExpectedConfirmation(yesNo, agent, target))
        {
            return Fail(
                "An unexpected confirmation appeared while lowering item quality " +
                $"(\"{DescribeYesNoPrompt(yesNo)}\"; agent: {DescribeAgent(agent)}; expected {Describe(target)}).");
        }
        if (ResolveExactSlot(target) == null)
        {
            return Fail(
                "The approved HQ stack changed before quality lowering was confirmed " +
                $"(expected {Describe(target)}; live {DescribeLiveSlot(target)}).");
        }
        if (yesNo->YesButton == null || !yesNo->YesButton->IsEnabled)
        {
            return Pending(
                ItemQualityLoweringAutomationState.WaitingForConfirmation,
                $"Waiting for the quality-lowering confirmation for {activeItemName} to become clickable.",
                $"yes button {(yesNo->YesButton == null ? "missing" : "disabled")}");
        }

        Event($"Confirming \"{DescribeYesNoPrompt(yesNo)}\" for {activeItemName} {Describe(target)} {elapsed:0.0}s after dispatch.");
        yesNo->YesButton->ClickAddonButton(&yesNo->AtkUnitBase);
        deadline = clock() + OperationTimeout;
        Snapshot = Snapshot with
        {
            State = ItemQualityLoweringAutomationState.WaitingForInventory,
            Message = $"Confirmed HQ-to-NQ conversion for {activeItemName}; verifying inventory evidence.",
        };
        return Snapshot;
    }

    private ItemQualityLoweringAutomationSnapshot VerifyActiveStack()
    {
        var target = activeStack ?? throw new InvalidOperationException("The active HQ stack is unavailable.");
        var stacks = Scan();
        var (normalAfter, highAfter) = CountQuality(target.ItemId, stacks);
        var expectedNormal = normalQualityBefore + target.Quantity;
        var expectedHigh = highQualityBefore - target.Quantity;
        if (normalAfter == expectedNormal && highAfter == expectedHigh)
        {
            Event(
                $"Verified {target.Quantity:N0} {activeItemName} lowered: " +
                $"NQ {normalQualityBefore}->{normalAfter}, HQ {highQualityBefore}->{highAfter}.");
            activeStack = null;
            Snapshot = new(
                ItemQualityLoweringAutomationState.Preparing,
                $"Verified {target.Quantity:N0} {activeItemName} lowered from HQ to NQ.",
                null,
                0,
                true);
            deadline = clock() + OperationTimeout;
            return Snapshot;
        }

        return Pending(
            ItemQualityLoweringAutomationState.WaitingForInventory,
            $"Waiting for inventory to show {activeItemName} lowered to NQ.",
            $"NQ {normalAfter}/{expectedNormal}, HQ {highAfter}/{expectedHigh}");
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
        var slot = ResolveSlot(expected);
        return slot != null &&
               slot->ItemId == expected.ItemId &&
               slot->Quantity == expected.Quantity &&
               slot->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality)
            ? slot
            : null;
    }

    private static unsafe InventoryItem* ResolveSlot(DalamudInventoryStack expected)
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

        return container->GetInventorySlot(expected.SlotIndex);
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
        string message,
        string? detail = null)
    {
        Observe(detail is null ? $"{state}: {message}" : $"{state}: {message} [{detail}]");
        Snapshot = Snapshot with { State = state, Message = message };
        return Snapshot;
    }

    private unsafe ItemQualityLoweringAutomationSnapshot Fail(string message)
    {
        var yesNo = gameGui.GetAddonByName<AddonSelectYesno>(SelectYesNoAddon, 1);
        var agent = AgentInventoryContext.Instance();
        if (activeStack is not null &&
            Snapshot.State == ItemQualityLoweringAutomationState.WaitingForConfirmation &&
            IsExpectedConfirmation(yesNo, agent, activeStack) &&
            yesNo->NoButton != null &&
            yesNo->NoButton->IsEnabled)
        {
            yesNo->NoButton->ClickAddonButton(&yesNo->AtkUnitBase);
            Event("Failure declined the owned quality-lowering confirmation.");
        }

        log?.Warning($"{LogPrefix} Failed in state {Snapshot.State}: {message}");
        activeStack = null;
        deadline = default;
        Snapshot = new(ItemQualityLoweringAutomationState.Failed, message, null, 0, false);
        return Snapshot;
    }

    /// <summary>Logs a per-frame observation only when it differs from the previous one.</summary>
    private void Observe(string observation)
    {
        if (observation == lastObservation)
            return;
        lastObservation = observation;
        log?.Information($"{LogPrefix} {observation}");
    }

    private void Event(string message)
    {
        lastObservation = null;
        log?.Information($"{LogPrefix} {message}");
    }

    private string DescribeActiveConditions()
    {
        if (condition is null)
            return "unavailable";
        var active = condition.AsReadOnlySet();
        return active.Count == 0 ? "none" : string.Join(", ", active);
    }

    private static string Describe(DalamudInventoryStack stack) =>
        $"{stack.Container}[{stack.SlotIndex}] item={stack.ItemId} qty={stack.Quantity} hq={stack.IsHighQuality}";

    private static unsafe string DescribeLiveSlot(DalamudInventoryStack expected)
    {
        var slot = ResolveSlot(expected);
        return slot == null
            ? "slot unavailable"
            : $"item={slot->ItemId} qty={slot->Quantity} hq={slot->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality)}";
    }

    private static unsafe string DescribeAgent(AgentInventoryContext* agent) =>
        agent == null
            ? "agent unavailable"
            : $"dialogType={agent->DialogType} target={agent->TargetInventoryId}[{agent->TargetInventorySlotId}] owner={agent->OwnerAddonId}";

    private static unsafe string DescribeYesNo(AddonSelectYesno* yesNo)
    {
        if (yesNo == null)
            return "SelectYesno absent";
        return $"SelectYesno ready={yesNo->AtkUnitBase.IsReady} visible={yesNo->AtkUnitBase.IsVisible} " +
               $"prompt=\"{DescribeYesNoPrompt(yesNo)}\"";
    }

    private static unsafe string DescribeYesNoPrompt(AddonSelectYesno* yesNo)
    {
        if (yesNo == null || yesNo->PromptText == null)
            return string.Empty;
        var text = yesNo->PromptText->NodeText.ToString().ReplaceLineEndings(" ").Trim();
        return text.Length <= 160 ? text : text[..160] + "...";
    }

    private static string DescribeState(ItemQualityLoweringAutomationState state) => state switch
    {
        ItemQualityLoweringAutomationState.Preparing => "preparing the next HQ stack",
        ItemQualityLoweringAutomationState.WaitingForConfirmation => "waiting for the quality-lowering confirmation",
        ItemQualityLoweringAutomationState.WaitingForInventory => "verifying the HQ-to-NQ inventory transition",
        _ => state.ToString(),
    };
}
