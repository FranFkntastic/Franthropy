using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using ECommons.Automation.UIInput;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
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
    private bool ownsUi;
    private bool menuSelectionSubmitted;
    private int stableMenuMisses;

    public bool MenuSelectionSubmitted => menuSelectionSubmitted;

    public DalamudDesynthesisUiTransaction(IGameGui gameGui) => this.gameGui = gameGui;

    public unsafe DalamudUiTransactionResult Begin(EquipmentInstanceFingerprint fingerprint)
    {
        var dialog = gameGui.GetAddonByName<AtkUnitBase>("SalvageDialog", 1);
        var menu = gameGui.GetAddonByName<AtkUnitBase>("ContextMenu", 1);
        if ((dialog != null && dialog->IsVisible) || (menu != null && menu->IsVisible))
            return DalamudUiTransactionResult.Fail("ConflictingUi", "Desynthesis or item context UI is already visible.");
        var opened = OpenExactSlotContextMenu(fingerprint);
        if (!opened.Success)
            return opened;
        ownsUi = true;
        menuSelectionSubmitted = false;
        stableMenuMisses = 0;
        return DalamudUiTransactionResult.Completed("ContextMenuRequested", "Opened the exact slot's item context menu.");
    }

    public unsafe DalamudUiTransactionResult AdvanceToConfirmation(EquipmentInstanceFingerprint fingerprint)
    {
        if (!ownsUi)
            return DalamudUiTransactionResult.Fail("UiOwnershipLost", "The desynthesis UI transaction is not owned.");
        var dialog = gameGui.GetAddonByName<AtkUnitBase>("SalvageDialog", 1);
        if (dialog == null || !dialog->IsVisible)
            return menuSelectionSubmitted ? DalamudUiTransactionResult.Pending("Waiting for SalvageDialog.") : SelectDesynthesis(fingerprint);
        var button = FindButton(dialog, ConfirmButton);
        if (button is null || !button->IsEnabled)
            return DalamudUiTransactionResult.Pending("Waiting for the visible Desynthesize button to become unambiguous and enabled.");
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
        return DalamudUiTransactionResult.Completed("ConfirmationSubmitted", "Clicked the visible Desynthesize button through the addon UI.");
    }

    public void Complete()
    {
        ownsUi = false;
        menuSelectionSubmitted = false;
        stableMenuMisses = 0;
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
        if (!Enum.TryParse<InventoryType>(fingerprint.Container, out var inventoryType))
            return DalamudUiTransactionResult.Fail("UnsupportedContainer", $"Inventory container {fingerprint.Container} is not recognized.");
        var context = AgentInventoryContext.Instance();
        if (context == null)
            return DalamudUiTransactionResult.Fail("InventoryContextUnavailable", "Inventory context UI is unavailable.");
        var ownerId = inventoryType.ToString().StartsWith("Armory", StringComparison.Ordinal) ? AgentId.ArmouryBoard : AgentId.Inventory;
        var owner = AgentModule.Instance()->GetAgentByInternalId(ownerId);
        if (owner == null)
            return DalamudUiTransactionResult.Fail("InventoryOwnerUnavailable", $"The normal {ownerId} UI is unavailable.");
        if (!owner->IsAgentActive())
            owner->Show();
        var addonId = owner->GetAddonId();
        if (addonId == 0)
            return DalamudUiTransactionResult.Fail("InventoryOwnerPending", $"Opened {ownerId}; retry after it is ready.");
        context->OpenForItemSlot(inventoryType, fingerprint.SlotIndex, 0, addonId);
        return DalamudUiTransactionResult.Completed("ContextMenuRequested", "Requested the exact slot's item context menu.");
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

    private unsafe DalamudUiTransactionResult SelectDesynthesis(EquipmentInstanceFingerprint fingerprint)
    {
        var menu = gameGui.GetAddonByName<AtkUnitBase>("ContextMenu", 1);
        if (menu == null || !menu->IsReady || !menu->IsVisible)
            return DalamudUiTransactionResult.Pending("Waiting for the exact slot's context menu.");
        if (!Enum.TryParse<InventoryType>(fingerprint.Container, out var inventoryType))
            return DalamudUiTransactionResult.Fail("UnsupportedContainer", $"Inventory container {fingerprint.Container} is not recognized.");
        var agent = AgentInventoryContext.Instance();
        if (agent == null || agent->TargetInventoryId != inventoryType || agent->TargetInventorySlotId != fingerprint.SlotIndex)
            return DalamudUiTransactionResult.Fail("UnexpectedContextMenu", "The visible context menu targets a different slot.");
        var labels = ReadLabels(agent);
        var match = DalamudContextMenuOptionParser.Find(labels, DesynthesisOption);
        if (!match.Success)
        {
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
        var values = stackalloc AtkValue[5];
        values[0] = new() { Type = AtkValueType.Int, Int = 0 };
        values[1] = new() { Type = AtkValueType.Int, Int = index };
        values[2] = new() { Type = AtkValueType.Int, Int = 0 };
        values[3] = new() { Type = AtkValueType.Int, Int = 0 };
        values[4] = new() { Type = AtkValueType.Int, Int = 0 };
        if (!menu->FireCallback(5, values, true))
            return DalamudUiTransactionResult.Fail("DesynthesisSelectionRejected", "ContextMenu rejected the Desynthesis selection.");
        menuSelectionSubmitted = true;
        return DalamudUiTransactionResult.Pending("Selected Desynthesis and am waiting for SalvageDialog.");
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
