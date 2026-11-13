using System.Diagnostics;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Franthropy.Dalamud.Automation.Inventory;

namespace Franthropy.Dalamud.Automation.Retainers;

internal sealed record RetainerDepositStart(bool Success, int Requested, int PlayerBefore, int RetainerBefore, string Code, string Message);

/// <summary>One dispatch, then read-only reconciliation shared by ordinary and crystal deposits.</summary>
internal static class RetainerDepositVerifier
{
    // Allow server round trips and delayed quantity-dialog construction independently.
    private const int VerificationBudgetMs = 3_000;
    // Re-read split inventory updates before classifying any mismatch.
    private const int SettleBudgetMs = 1_000;
    // A bounded diagnostic tail detects thresholds that are too short, never resends.
    private const int LateObservationBudgetMs = 1_000;
    private const int MinimumPollsPerPhase = 10;
    // Agent/container visibility can flicker while the dialog opens.
    private const int PreconditionLossToleranceMs = 500;
    private static readonly InventoryType[] PlayerBags = [InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4];

    internal static unsafe bool QuantityPopupVisible(IGameGui gui)
    {
        var numeric = gui.GetAddonByName<AtkUnitBase>("InputNumeric", 1);
        return numeric != null && numeric->IsVisible;
    }

    internal static unsafe bool PreconditionsAvailable(DalamudInventoryStack stack)
    {
        var manager = InventoryManager.Instance();
        var module = AgentModule.Instance();
        var agent = module == null ? null : module->GetAgentByInternalId(AgentId.Retainer);
        if (manager == null || agent == null || !agent->IsAgentActive())
            return false;
        foreach (var type in Containers(stack))
        {
            var container = manager->GetInventoryContainer(type);
            if (container == null || !container->IsLoaded)
                return false;
        }
        return true;
    }

    private static IEnumerable<InventoryType> Containers(DalamudInventoryStack stack) =>
        stack.Container == InventoryType.Crystals
            ? [InventoryType.Crystals, InventoryType.RetainerCrystals]
            : PlayerBags.Concat(DalamudRetainerInventory.OrdinaryItemContainers);

    private static (int Player, int Retainer)? Observe(DalamudInventoryStack stack)
    {
        if (!PreconditionsAvailable(stack))
            return null;
        var crystals = stack.Container == InventoryType.Crystals;
        int Count(InventoryType type) => DalamudInventoryStackScanner.CountLoadedItem(type, stack.ItemId, crystals ? null : stack.IsHighQuality);
        return crystals ? (Count(InventoryType.Crystals), Count(InventoryType.RetainerCrystals))
            : (PlayerBags.Sum(Count), DalamudRetainerInventory.OrdinaryItemContainers.Sum(Count));
    }

    public static async Task<RetainerDepositResult> DepositAsync(
        DalamudInventoryStack stack, IFramework framework, IGameGui gui, IPluginLog log, IGameInventory? inventory,
        Func<RetainerDepositStart> begin, Func<int, RetainerDepositResult> submit, CancellationToken token)
    {
        using var mutation = inventory is null ? null : new RetainerRetrievalMutationTracker(inventory, stack.ItemId, stack.IsHighQuality);
        var elapsed = Stopwatch.StartNew();
        var buffered = new List<string>();
        var pending = await framework.RunOnTick(begin, cancellationToken: token).ConfigureAwait(false);
        if (!pending.Success || pending.Requested == 0)
        {
            if (!pending.Success) log.Warning("Deposit {Code}: {Message}", pending.Code, pending.Message);
            return new(pending.Success, 0, pending.Code, pending.Message);
        }
        var expected = pending.Requested;
        var quantityExpected = stack.Quantity > 1;
        var seen = false;
        var submitted = false;
        var polls = 0;
        var player = pending.PlayerBefore;
        var retainer = pending.RetainerBefore;
        var changed = false;
        long? lostSince = null;
        var available = true;
        var phase = Stopwatch.StartNew();
        var phasePolls = 0;
        var settling = false;
        string? refusal = null;
        void Trace(string message)
        {
            buffered.Add(message);
            log.Information("{DepositDiagnostic}", message);
        }
        string Evidence() => $"item={stack.ItemId} source={stack.Container}/{stack.SlotIndex} expected={expected}; budget={VerificationBudgetMs}ms settle={SettleBudgetMs}ms elapsed={elapsed.ElapsedMilliseconds}ms polls={polls}; player={pending.PlayerBefore}->{player} retainer={pending.RetainerBefore}->{retainer}; quantity expected={quantityExpected} seen={seen} submitted={submitted}; inventoryEvents={mutation?.Snapshot().ToString() ?? "unavailable"}; preconditions={available}; not retried";
        Trace($"DepositDispatch {Evidence()}");
        try
        {
            while (true)
            {
                var observation = await framework.RunOnTick(() => Observe(stack), cancellationToken: token).ConfigureAwait(false);
                polls++;
                phasePolls++;
                available = observation.HasValue;
                if (observation is { } counts)
                {
                    lostSince = null;
                    if (player != counts.Player || retainer != counts.Retainer)
                    {
                        player = counts.Player;
                        retainer = counts.Retainer;
                        changed = true;
                        Trace($"DepositObservation {Evidence()}");
                    }
                    if (RetainerDepositObservation.Matches(expected, pending.PlayerBefore, player, pending.RetainerBefore, retainer))
                        return new(true, expected, "TransferVerified", $"Deposit verified: {Evidence()}");
                }
                else
                    lostSince ??= elapsed.ElapsedMilliseconds;

                if (lostSince is { } lost && elapsed.ElapsedMilliseconds - lost >= PreconditionLossToleranceMs)
                    return await Failure("VerificationPreconditionsLost").ConfigureAwait(false);

                // Submission is allowed only during the quantity phase, and at most once.
                if (!settling && !submitted && available)
                {
                    var wasSeen = seen;
                    seen |= await framework.RunOnTick(() => QuantityPopupVisible(gui), cancellationToken: token).ConfigureAwait(false);
                    if (seen && !wasSeen) Trace($"DepositQuantitySeen {Evidence()}");
                    var result = await framework.RunOnTick(() => submit(expected), cancellationToken: token).ConfigureAwait(false);
                    if (result.Success)
                    {
                        submitted = true;
                        expected = result.Transferred;
                        Trace($"DepositQuantitySubmitted {Evidence()}");
                        phase.Restart();
                        phasePolls = 0;
                    }
                    else if (result.Code == "NoCapacity")
                    {
                        refusal = result.Message;
                        return await Failure(changed ? "DepositDeltaMismatch" : "NoCapacity").ConfigureAwait(false);
                    }
                }

                if (phase.ElapsedMilliseconds >= (settling ? SettleBudgetMs : VerificationBudgetMs) && phasePolls >= MinimumPollsPerPhase)
                {
                    if (!settling)
                    {
                        settling = true;
                        phase.Restart();
                        phasePolls = 0;
                        Trace($"DepositSettle {Evidence()}");
                    }
                    else
                    {
                        var events = mutation?.Snapshot();
                        var movement = changed || events is { } e && (e.PlayerDelta != 0 || e.RetainerDelta != 0);
                        return await Failure(!available ? "VerificationPreconditionsLost" : movement ? "DepositDeltaMismatch"
                            : quantityExpected && !seen && !submitted ? "DepositQuantityPopupNotOpened"
                            : "VerificationWindowExpired").ConfigureAwait(false);
                    }
                }
                await framework.DelayTicks(1, token).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            foreach (var entry in buffered) log.Warning("{DepositDiagnostic}", entry);
            log.Warning(exception, "Deposit interrupted after dispatch; {Evidence}", Evidence());
            throw;
        }

        async Task<RetainerDepositResult> Failure(string code)
        {
            var message = $"{code}: {Evidence()}. {refusal}";
            foreach (var entry in buffered) log.Warning("{DepositDiagnostic}", entry);
            log.Warning("{DepositFailure}", message);
            var verdictAt = elapsed.ElapsedMilliseconds;
            // This bounded tail is awaited so no unowned task outlives the operation or event subscription.
            var late = Stopwatch.StartNew();
            while (late.ElapsedMilliseconds < LateObservationBudgetMs)
            {
                var observation = await framework.RunOnTick(() => Observe(stack), cancellationToken: token).ConfigureAwait(false);
                if (observation is { } counts && RetainerDepositObservation.Matches(expected, pending.PlayerBefore, counts.Player, pending.RetainerBefore, counts.Retainer))
                {
                    log.Warning("DepositLateConfirmation item={Item} lateBy={Late}ms verdict={Code} player={BeforePlayer}->{Player} retainer={BeforeRetainer}->{Retainer}",
                        stack.ItemId, elapsed.ElapsedMilliseconds - verdictAt, code, pending.PlayerBefore, counts.Player, pending.RetainerBefore, counts.Retainer);
                    break;
                }
                await framework.DelayTicks(1, token).ConfigureAwait(false);
            }
            return new(false, 0, code, message);
        }
    }
}
