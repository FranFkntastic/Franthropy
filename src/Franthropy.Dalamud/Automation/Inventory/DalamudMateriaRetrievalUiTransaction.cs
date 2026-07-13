using Dalamud.Plugin.Services;
using Dalamud.Utility;
using ECommons.Automation.UIInput;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Franthropy.Dalamud.Automation;
using Franthropy.Dalamud.Equipment;

namespace Franthropy.Dalamud.Automation.Inventory;

/// <summary>Drives one normal, visible Retrieve Materia interaction for an exact inventory slot.</summary>
public sealed class DalamudMateriaRetrievalUiTransaction
{
    public static readonly DalamudContextMenuOptionSpec RetrieveMateriaOption = new(
        "RetrieveMateria",
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Retrieve Materia",
            "マテリア回収",
            "Materia zurückgewinnen",
            "Retirer des matérias",
        });

    private static readonly DalamudContextMenuOptionSpec BeginButton = new(
        "BeginMateriaRetrieval",
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Retrieve", "Begin" });

    private readonly IGameGui gameGui;
    private readonly Func<EquipmentInstanceFingerprint, bool> exactIdentityStillValid;
    private readonly DalamudExactSlotContextMenuOpener contextMenuOpener = new();
    private readonly DalamudUiStabilityGate menuStability = new(6);
    private bool ownsUi;
    private bool menuSelectionSubmitted;
    private int stableMenuMisses;

    public DalamudMateriaRetrievalUiTransaction(
        IGameGui gameGui,
        Func<EquipmentInstanceFingerprint, bool>? exactIdentityStillValid = null)
    {
        this.gameGui = gameGui;
        this.exactIdentityStillValid = exactIdentityStillValid ?? (_ => true);
    }

    public unsafe DalamudUiTransactionResult Begin(EquipmentInstanceFingerprint fingerprint)
    {
        if (fingerprint.MateriaIds.Count == 0)
            return DalamudUiTransactionResult.Fail("MateriaAbsent", "The exact item no longer has attached materia.");
        if (IsVisible("ContextMenu") || IsVisible("MateriaRetrieveDialog"))
            return DalamudUiTransactionResult.Fail("ConflictingUi", "An item context or materia retrieval UI is already visible.");

        var opened = contextMenuOpener.Begin(fingerprint);
        if (!opened.Success)
            return opened;
        ownsUi = true;
        menuSelectionSubmitted = false;
        stableMenuMisses = 0;
        menuStability.Reset();
        return opened;
    }

    public unsafe DalamudUiTransactionResult Advance(
        EquipmentInstanceFingerprint fingerprint,
        Func<bool>? mutationStillAuthorized = null)
    {
        if (!ownsUi)
            return DalamudUiTransactionResult.Fail("UiOwnershipLost", "The materia retrieval UI transaction is not owned.");

        var dialog = gameGui.GetAddonByName<AtkUnitBase>("MateriaRetrieveDialog", 1);
        if (dialog == null || !dialog->IsReady || !dialog->IsVisible)
        {
            if (menuSelectionSubmitted)
                return DalamudUiTransactionResult.Pending("Waiting for MateriaRetrieveDialog.");
            var opened = contextMenuOpener.Advance(fingerprint);
            if (!opened.Success)
                return opened;
            return SelectRetrieveMateria(fingerprint, mutationStillAuthorized);
        }

        var button = FindButton(dialog, BeginButton);
        if (button == null)
            button = dialog->GetComponentButtonById(17);
        if (button == null || !button->IsEnabled)
            return DalamudUiTransactionResult.Pending("Waiting for the visible materia retrieval button to become enabled.");
        if (mutationStillAuthorized?.Invoke() == false)
            return DalamudUiTransactionResult.Fail("MutationAuthorizationChanged", "Materia retrieval authorization or automation ownership changed before confirmation.");
        if (!exactIdentityStillValid(fingerprint))
            return DalamudUiTransactionResult.Fail("ExactIdentityChanged", "The approved exact item changed before materia retrieval confirmation.");
        button->ClickAddonButton(dialog);
        return DalamudUiTransactionResult.Completed("RetrievalSubmitted", "Clicked the visible materia retrieval button through the addon UI.");
    }

    public void Complete()
    {
        ownsUi = false;
        menuSelectionSubmitted = false;
        stableMenuMisses = 0;
        menuStability.Reset();
        contextMenuOpener.Reset();
    }

    public unsafe bool IsUiSettled() => !IsVisible("ContextMenu") && !IsVisible("MateriaRetrieveDialog");

    public unsafe void CloseOwnedUi()
    {
        if (!ownsUi)
            return;
        var menu = gameGui.GetAddonByName<AtkUnitBase>("ContextMenu", 1);
        if (menu != null && menu->IsVisible)
            menu->Close(true);
        var dialog = gameGui.GetAddonByName<AtkUnitBase>("MateriaRetrieveDialog", 1);
        if (dialog != null && dialog->IsVisible)
        {
            var cancel = dialog->GetComponentButtonById(18);
            if (cancel != null && cancel->IsEnabled)
                cancel->ClickAddonButton(dialog);
        }
        if (IsUiSettled())
            Complete();
    }

    private unsafe DalamudUiTransactionResult SelectRetrieveMateria(
        EquipmentInstanceFingerprint fingerprint,
        Func<bool>? mutationStillAuthorized)
    {
        var menu = gameGui.GetAddonByName<AtkUnitBase>("ContextMenu", 1);
        if (menu == null || !menu->IsReady || !menu->IsVisible)
        {
            menuStability.Observe(false);
            return DalamudUiTransactionResult.Pending("Waiting for the exact slot's context menu.");
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
        var match = DalamudContextMenuOptionParser.Find(labels, RetrieveMateriaOption);
        if (!match.Success)
        {
            menuStability.Observe(false);
            if (match.Code != "OptionAmbiguous" && ++stableMenuMisses < 8)
                return DalamudUiTransactionResult.Pending("Waiting for stable Retrieve Materia context-menu labels.");
            return DalamudUiTransactionResult.Fail(
                match.Code == "OptionAmbiguous" ? "RetrieveMateriaEntryAmbiguous" : "RetrieveMateriaEntryUnavailable",
                match.Code == "OptionAmbiguous" ? "Multiple context-menu entries matched Retrieve Materia." : "The exact slot's context menu does not offer Retrieve Materia.");
        }
        if (match.Index >= agent->ContextItemCount || agent->IsContextItemDisabled(match.Index))
            return DalamudUiTransactionResult.Fail("RetrieveMateriaEntryDisabled", "Retrieve Materia is disabled for the exact item.");
        if (!menuStability.Observe(true))
            return DalamudUiTransactionResult.Pending(
                $"Waiting for Retrieve Materia to remain stable ({menuStability.ObservedConsecutiveFrames}/{menuStability.RequiredConsecutiveFrames} frames).");
        if (mutationStillAuthorized?.Invoke() == false)
            return DalamudUiTransactionResult.Fail("MutationAuthorizationChanged", "Materia retrieval authorization or automation ownership changed before menu selection.");
        if (!exactIdentityStillValid(fingerprint))
            return DalamudUiTransactionResult.Fail("ExactIdentityChanged", "The approved exact item changed before selecting Retrieve Materia.");

        var values = stackalloc AtkValue[5];
        values[0] = new() { Type = AtkValueType.Int, Int = 0 };
        values[1] = new() { Type = AtkValueType.Int, Int = match.Index };
        values[2] = new() { Type = AtkValueType.Int, Int = 0 };
        values[3] = new() { Type = AtkValueType.Int, Int = 0 };
        values[4] = new() { Type = AtkValueType.Int, Int = 0 };
        if (!menu->FireCallback(5, values, true))
            return DalamudUiTransactionResult.Fail("RetrieveMateriaSelectionRejected", "ContextMenu rejected the Retrieve Materia selection.");
        menuSelectionSubmitted = true;
        return DalamudUiTransactionResult.Pending("Selected Retrieve Materia and am waiting for its dialog.");
    }

    private unsafe bool IsVisible(string name)
    {
        var addon = gameGui.GetAddonByName<AtkUnitBase>(name, 1);
        return addon != null && addon->IsVisible;
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
            if (button == null || button->ButtonTextNode == null ||
                !DalamudContextMenuOptionParser.Find([button->ButtonTextNode->NodeText.ExtractText()], option).Success)
                continue;
            if (match != null)
                return null;
            match = button;
        }
        return match;
    }
}
