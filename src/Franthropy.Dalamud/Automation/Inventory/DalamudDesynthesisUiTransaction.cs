using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using ECommons.Automation.UIInput;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Franthropy.Dalamud.Automation;
using Franthropy.Dalamud.Equipment;

namespace Franthropy.Dalamud.Automation.Inventory;

public sealed class DalamudDesynthesisUiTransaction
{
    private static readonly DalamudContextMenuOptionSpec DesynthesisOption = new(
        "Desynthesis",
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Desynthesis", "Desynthesize" });
    private static readonly DalamudContextMenuOptionSpec ConfirmButton = new(
        "ConfirmDesynthesis",
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Desynthesize", "Desynthesis" });
    private static readonly DalamudContextMenuOptionSpec CancelButton = new(
        "Cancel",
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Cancel" });
    private readonly IGameGui gameGui;
    private readonly DalamudExactSlotContextMenuOpener contextMenuOpener = new();
    private readonly DalamudUiStabilityGate menuStability = new(6);
    private bool ownsUi;
    private bool menuSelectionSubmitted;
    private int stableMenuMisses;
    private int framesWaitingForDialog;

    public bool MenuSelectionSubmitted => menuSelectionSubmitted;
    public string Status { get; private set; } = "Idle.";

    public DalamudDesynthesisUiTransaction(IGameGui gameGui) => this.gameGui = gameGui;

    public unsafe DalamudUiTransactionResult Begin(EquipmentInstanceFingerprint fingerprint)
    {
        var dialog = gameGui.GetAddonByName<AtkUnitBase>("SalvageDialog", 1);
        var menu = gameGui.GetAddonByName<AtkUnitBase>("ContextMenu", 1);
        if ((dialog != null && dialog->IsVisible) || (menu != null && menu->IsVisible))
            return DalamudUiTransactionResult.Fail("ConflictingUi", "Desynthesis or item context UI is already visible.");
        var opened = contextMenuOpener.Begin(fingerprint);
        if (!opened.Success)
            return opened;
        ownsUi = true;
        menuSelectionSubmitted = false;
        stableMenuMisses = 0;
        framesWaitingForDialog = 0;
        menuStability.Reset();
        Status = opened.Message;
        return DalamudUiTransactionResult.Completed("ContextMenuRequested", Status);
    }

    public unsafe DalamudUiTransactionResult AdvanceToConfirmation(EquipmentInstanceFingerprint fingerprint, Func<bool> mutationStillAuthorized)
    {
        ArgumentNullException.ThrowIfNull(mutationStillAuthorized);
        if (!ownsUi)
            return DalamudUiTransactionResult.Fail("UiOwnershipLost", "The desynthesis UI transaction is not owned.");
        var dialog = gameGui.GetAddonByName<AtkUnitBase>("SalvageDialog", 1);
        if (dialog == null || !dialog->IsVisible)
        {
            if (!menuSelectionSubmitted)
            {
                var opened = contextMenuOpener.Advance(fingerprint);
                Status = opened.Message;
                if (!opened.Success)
                    return opened;
                return SelectDesynthesis(fingerprint, mutationStillAuthorized);
            }
            framesWaitingForDialog++;
            var menu = gameGui.GetAddonByName<AtkUnitBase>("ContextMenu", 1);
            Status = menu != null && menu->IsVisible
                ? $"The Desynthesis selection was submitted; the context menu remains visible while waiting for SalvageDialog ({framesWaitingForDialog} frame(s))."
                : $"The Desynthesis selection was submitted and the context menu closed, but SalvageDialog has not appeared ({framesWaitingForDialog} frame(s)).";
            return DalamudUiTransactionResult.Pending(Status);
        }
        Status = "SalvageDialog is visible; waiting for its semantic confirmation button.";
        var button = FindButton(dialog, ConfirmButton);
        if (button is null || !button->IsEnabled)
        {
            Status = "SalvageDialog is visible, but its Desynthesize button is not yet unambiguous and enabled.";
            return DalamudUiTransactionResult.Pending(Status);
        }
        if (!mutationStillAuthorized())
            return DalamudUiTransactionResult.Fail("MutationAuthorizationLost", "The approved batch or automation ownership changed before desynthesis confirmation.");
        try
        {
            button->ClickAddonButton(dialog);
        }
        catch (NullReferenceException)
        {
            // The addon may invalidate part of its button tree while the normal callback is
            // unwinding, before the visible flag changes. The click was already dispatched;
            // the caller's exact-slot transition observation remains the completion oracle.
        }
        Status = "Clicked the visible Desynthesize button through the addon UI.";
        return DalamudUiTransactionResult.Completed("ConfirmationSubmitted", Status);
    }

    public void Complete()
    {
        ownsUi = false;
        menuSelectionSubmitted = false;
        stableMenuMisses = 0;
        framesWaitingForDialog = 0;
        menuStability.Reset();
        contextMenuOpener.Reset();
        Status = "Idle.";
    }

    public unsafe bool IsUiSettled()
    {
        var menu = gameGui.GetAddonByName<AtkUnitBase>("ContextMenu", 1);
        var dialog = gameGui.GetAddonByName<AtkUnitBase>("SalvageDialog", 1);
        return (menu == null || !menu->IsVisible) && (dialog == null || !dialog->IsVisible);
    }

    public unsafe void CloseOwnedUi()
    {
        if (!ownsUi)
            return;
        CloseVisibleUi();
        if (IsUiSettled())
            Complete();
    }

    public unsafe void CloseVisibleUi()
    {
        var menu = gameGui.GetAddonByName<AtkUnitBase>("ContextMenu", 1);
        if (menu != null && menu->IsVisible)
            menu->Close(true);
        var dialog = gameGui.GetAddonByName<AtkUnitBase>("SalvageDialog", 1);
        if (dialog != null && dialog->IsVisible)
        {
            var cancel = FindButton(dialog, CancelButton);
            if (cancel is not null && cancel->IsEnabled)
                cancel->ClickAddonButton(dialog);
        }
    }

    public unsafe DalamudUiTransactionResult OpenExactSlotContextMenu(EquipmentInstanceFingerprint fingerprint)
    {
        if (ownsUi)
            return DalamudUiTransactionResult.Fail("DesynthesisUiAlreadyOwned", "A desynthesis UI transaction is already active.");
        menuStability.Reset();
        var result = contextMenuOpener.Begin(fingerprint);
        if (result.Success)
        {
            ownsUi = true;
            menuSelectionSubmitted = false;
            stableMenuMisses = 0;
            framesWaitingForDialog = 0;
        }
        Status = result.Message;
        return result;
    }

    public unsafe string DescribeContextMenu()
    {
        var addon = gameGui.GetAddonByName<AtkUnitBase>("ContextMenu", 1);
        var agent = AgentInventoryContext.Instance();
        if (addon == null || !addon->IsReady || !addon->IsVisible || agent == null)
            return "ContextMenu is not ready or visible.";
        var labels = ReadLabels(agent);
        var entries = Enumerable.Range(0, Math.Min(agent->ContextItemCount, labels.Count))
            .Select(index => $"[{index}] {labels[index]} (disabled={agent->IsContextItemDisabled(index)})");
        return $"target={agent->TargetInventoryId}:{agent->TargetInventorySlotId}; {string.Join(" | ", entries)}";
    }

    public unsafe DalamudUiTransactionResult ProbeContextMenu(EquipmentInstanceFingerprint fingerprint)
    {
        var opened = contextMenuOpener.Advance(fingerprint);
        Status = opened.Message;
        if (!opened.Success)
            return opened;
        if (!Enum.TryParse<InventoryType>(fingerprint.Container, out var inventoryType))
            return DalamudUiTransactionResult.Fail("UnsupportedContainer", $"Inventory container {fingerprint.Container} is not recognized.");
        var menu = gameGui.GetAddonByName<AtkUnitBase>("ContextMenu", 1);
        var agent = AgentInventoryContext.Instance();
        if (menu == null || !menu->IsReady || !menu->IsVisible || agent == null)
        {
            menuStability.Observe(false);
            return DalamudUiTransactionResult.Pending("Waiting for the exact slot's context menu.");
        }
        if (agent->TargetInventoryId != inventoryType || agent->TargetInventorySlotId != fingerprint.SlotIndex)
        {
            menuStability.Observe(false);
            return DalamudUiTransactionResult.Fail("UnexpectedContextMenu", "The visible context menu targets a different slot.");
        }
        var labels = ReadLabels(agent);
        var match = DalamudContextMenuOptionParser.Find(labels, DesynthesisOption);
        if (!match.Success)
        {
            menuStability.Observe(false);
            return match.Code == "OptionAmbiguous"
                ? DalamudUiTransactionResult.Fail("DesynthesisEntryAmbiguous", "Multiple context-menu entries matched Desynthesis.")
                : DalamudUiTransactionResult.Pending("Waiting for stable Desynthesis context-menu labels.");
        }
        if (match.Index >= agent->ContextItemCount || agent->IsContextItemDisabled(match.Index))
            return DalamudUiTransactionResult.Fail("DesynthesisEntryDisabled", "The Desynthesis entry is unavailable or disabled.");
        if (!menuStability.Observe(true))
            return DalamudUiTransactionResult.Pending(
                $"The Desynthesis entry is enabled; verifying stability ({menuStability.ObservedConsecutiveFrames}/{menuStability.RequiredConsecutiveFrames} frames).");
        return DalamudUiTransactionResult.Completed("DesynthesisProbePassed", "The exact item's normal context menu offers an enabled Desynthesis command.");
    }

    private unsafe DalamudUiTransactionResult SelectDesynthesis(EquipmentInstanceFingerprint fingerprint, Func<bool> mutationStillAuthorized)
    {
        var menu = gameGui.GetAddonByName<AtkUnitBase>("ContextMenu", 1);
        if (menu == null || !menu->IsReady || !menu->IsVisible)
        {
            menuStability.Observe(false);
            Status = "Waiting for the exact slot's context menu to become visible and ready.";
            return DalamudUiTransactionResult.Pending(Status);
        }
        if (!Enum.TryParse<InventoryType>(fingerprint.Container, out var inventoryType))
            return DalamudUiTransactionResult.Fail("UnsupportedContainer", $"Inventory container {fingerprint.Container} is not recognized.");
        var agent = AgentInventoryContext.Instance();
        if (agent == null || agent->TargetInventoryId != inventoryType || agent->TargetInventorySlotId != fingerprint.SlotIndex)
        {
            menuStability.Observe(false);
            return DalamudUiTransactionResult.Fail("UnexpectedContextMenu", "The visible context menu targets a different slot.");
        }
        var labels = ReadLabels(agent);
        var match = DalamudContextMenuOptionParser.Find(labels, DesynthesisOption);
        if (!match.Success)
        {
            menuStability.Observe(false);
            if (match.Code != "OptionAmbiguous" && ++stableMenuMisses < 8)
                return DalamudUiTransactionResult.Pending(
                    $"Waiting for stable context-menu labels ({labels.Count}/{agent->ContextItemCount} observed).");
            return DalamudUiTransactionResult.Fail(
                match.Code == "OptionAmbiguous" ? "DesynthesisEntryAmbiguous" : "DesynthesisEntryUnavailable",
                match.Code == "OptionAmbiguous" ? "Multiple context-menu entries matched Desynthesis." : "The exact slot's context menu does not offer Desynthesis.");
        }
        stableMenuMisses = 0;
        var index = match.Index;
        if (index >= agent->ContextItemCount)
            return DalamudUiTransactionResult.Fail("DesynthesisEntryUnavailable", "The exact slot's context menu does not offer Desynthesis.");
        if (agent->IsContextItemDisabled(index))
            return DalamudUiTransactionResult.Fail("DesynthesisEntryDisabled", "The Desynthesis entry is disabled.");
        if (!menuStability.Observe(true))
        {
            Status = $"The exact Desynthesis menu entry is ready; waiting for UI stability ({menuStability.ObservedConsecutiveFrames}/{menuStability.RequiredConsecutiveFrames} frames).";
            return DalamudUiTransactionResult.Pending(Status);
        }
        if (!mutationStillAuthorized())
            return DalamudUiTransactionResult.Fail("MutationAuthorizationLost", "The approved batch or automation ownership changed before desynthesis selection.");
        var values = stackalloc AtkValue[5];
        values[0] = new() { Type = AtkValueType.Int, Int = 0 };
        values[1] = new() { Type = AtkValueType.Int, Int = index };
        values[2] = new() { Type = AtkValueType.Int, Int = 0 };
        values[3] = new() { Type = AtkValueType.Int, Int = 0 };
        values[4] = new() { Type = AtkValueType.Int, Int = 0 };
        if (!menu->FireCallback(5, values, true))
            return DalamudUiTransactionResult.Fail("DesynthesisSelectionRejected", "ContextMenu rejected the Desynthesis selection.");
        menuSelectionSubmitted = true;
        framesWaitingForDialog = 0;
        Status = $"Submitted the stable Desynthesis menu entry after {menuStability.ObservedConsecutiveFrames} ready frames; waiting for SalvageDialog.";
        return DalamudUiTransactionResult.Pending(Status);
    }

    private static unsafe IReadOnlyList<string> ReadLabels(AgentInventoryContext* agent)
    {
        var labels = new List<string>();
        foreach (var parameter in agent->EventParams)
            if (parameter.Type is AtkValueType.String or AtkValueType.ManagedString or AtkValueType.WideString or AtkValueType.ConstString)
                labels.Add(parameter.GetValueAsString());
        return labels;
    }

    private static unsafe AtkComponentButton* FindButton(AtkUnitBase* addon, DalamudContextMenuOptionSpec option)
    {
        AtkComponentButton* match = null;
        for (uint componentId = 1; componentId <= 100; componentId++)
        {
            var button = addon->GetComponentButtonById(componentId);
            if (button == null || button->ButtonTextNode == null)
                continue;
            var label = button->ButtonTextNode->NodeText.ExtractText();
            if (!DalamudContextMenuOptionParser.Find([label], option).Success)
                continue;
            if (match != null)
                return null;
            match = button;
        }
        return match;
    }
}
