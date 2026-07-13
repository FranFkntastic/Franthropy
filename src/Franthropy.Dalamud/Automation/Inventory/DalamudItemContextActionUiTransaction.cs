using Dalamud.Plugin.Services;
using Dalamud.Utility;
using ECommons.Automation.UIInput;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Franthropy.Dalamud.Automation;
using Franthropy.Dalamud.Equipment;

namespace Franthropy.Dalamud.Automation.Inventory;

/// <summary>
/// Owns one semantic action selected from an exact inventory slot's normal context menu.
/// It never calls the underlying inventory mutation function directly.
/// </summary>
public sealed class DalamudItemContextActionUiTransaction
{
    private readonly IGameGui gameGui;
    private readonly DalamudContextMenuOptionSpec option;
    private readonly Func<EquipmentInstanceFingerprint, bool> exactIdentityStillValid;
    private readonly string? requiredVisibleAddon;
    private readonly Func<EquipmentInstanceFingerprint, string, bool>? expectedConfirmation;
    private readonly DalamudExactSlotContextMenuOpener contextMenuOpener = new();
    private readonly DalamudUiStabilityGate menuStability = new(6);
    private bool ownsUi;
    private bool menuSelectionSubmitted;
    private bool confirmationSubmitted;
    private int stableMenuMisses;

    public DalamudItemContextActionUiTransaction(
        IGameGui gameGui,
        DalamudContextMenuOptionSpec option,
        Func<EquipmentInstanceFingerprint, bool> exactIdentityStillValid,
        string? requiredVisibleAddon = null,
        Func<EquipmentInstanceFingerprint, string, bool>? expectedConfirmation = null)
    {
        this.gameGui = gameGui;
        this.option = option ?? throw new ArgumentNullException(nameof(option));
        this.exactIdentityStillValid = exactIdentityStillValid ?? throw new ArgumentNullException(nameof(exactIdentityStillValid));
        this.requiredVisibleAddon = requiredVisibleAddon;
        this.expectedConfirmation = expectedConfirmation;
    }

    public unsafe DalamudUiTransactionResult Begin(EquipmentInstanceFingerprint fingerprint)
    {
        if (ownsUi)
            return DalamudUiTransactionResult.Fail("ContextActionAlreadyOwned", $"A {option.SemanticName} transaction is already active.");
        if (IsVisible("ContextMenu") || IsVisible("SelectYesno"))
            return DalamudUiTransactionResult.Fail("ConflictingUi", "An item context menu or confirmation was already visible.");
        if (!RequiredOwnerIsVisible())
            return DalamudUiTransactionResult.Fail("RequiredUiUnavailable", $"{requiredVisibleAddon} must be open before {option.SemanticName}.");
        if (!exactIdentityStillValid(fingerprint))
            return DalamudUiTransactionResult.Fail("ExactIdentityChanged", "The approved exact item changed before its context menu was opened.");
        var opened = contextMenuOpener.Begin(fingerprint);
        if (!opened.Success)
            return opened;
        ownsUi = true;
        menuSelectionSubmitted = false;
        confirmationSubmitted = false;
        stableMenuMisses = 0;
        menuStability.Reset();
        return DalamudUiTransactionResult.Completed("ContextMenuRequested", $"Opened the exact slot's context menu for {option.SemanticName}.");
    }

    public unsafe DalamudUiTransactionResult Advance(EquipmentInstanceFingerprint fingerprint, Func<bool> mutationStillAuthorized)
    {
        ArgumentNullException.ThrowIfNull(mutationStillAuthorized);
        if (!ownsUi)
            return DalamudUiTransactionResult.Fail("ContextActionOwnershipLost", $"The {option.SemanticName} UI transaction is not owned.");
        if (!RequiredOwnerIsVisible())
            return DalamudUiTransactionResult.Fail("RequiredUiOwnershipLost", $"The required {requiredVisibleAddon} UI closed during {option.SemanticName}.");

        var yesNo = gameGui.GetAddonByName<AddonSelectYesno>("SelectYesno", 1);
        if (yesNo != null && yesNo->AtkUnitBase.IsReady && yesNo->AtkUnitBase.IsVisible)
        {
            if (!menuSelectionSubmitted)
                return DalamudUiTransactionResult.Fail("UnexpectedConfirmation", "A confirmation appeared before the owned context-menu action was submitted.");
            if (confirmationSubmitted)
                return DalamudUiTransactionResult.Pending("Waiting for the submitted confirmation to complete.");
            if (expectedConfirmation is null)
                return DalamudUiTransactionResult.Fail("UnexpectedConfirmation", $"{option.SemanticName} opened an unapproved confirmation dialog.");
            var observed = yesNo->PromptText->NodeText.ExtractText().Trim();
            if (!expectedConfirmation(fingerprint, observed))
                return DalamudUiTransactionResult.Fail("UnexpectedConfirmation", $"{option.SemanticName} opened an unexpected confirmation: {observed}");
            if (!exactIdentityStillValid(fingerprint))
                return DalamudUiTransactionResult.Fail("ExactIdentityChanged", "The approved exact item changed before confirmation.");
            if (!mutationStillAuthorized())
                return DalamudUiTransactionResult.Fail("MutationAuthorizationLost", $"The approved batch or automation ownership changed before {option.SemanticName} confirmation.");
            if (yesNo->YesButton == null || !yesNo->YesButton->IsEnabled)
                return DalamudUiTransactionResult.Pending("Waiting for the approved confirmation button.");
            yesNo->YesButton->ClickAddonButton(&yesNo->AtkUnitBase);
            confirmationSubmitted = true;
            return DalamudUiTransactionResult.Pending("Submitted the approved visible confirmation.");
        }

        if (menuSelectionSubmitted)
            return DalamudUiTransactionResult.Pending($"Waiting for {option.SemanticName} to change the exact inventory slot.");
        if (!exactIdentityStillValid(fingerprint))
            return DalamudUiTransactionResult.Fail("ExactIdentityChanged", "The approved exact item changed before context-menu selection.");
        if (!mutationStillAuthorized())
            return DalamudUiTransactionResult.Fail("MutationAuthorizationLost", $"The approved batch or automation ownership changed before {option.SemanticName} selection.");
        var opened = contextMenuOpener.Advance(fingerprint);
        if (!opened.Success)
            return opened;
        return SelectOption(fingerprint);
    }

    public unsafe DalamudUiTransactionResult Probe(EquipmentInstanceFingerprint fingerprint)
    {
        if (!ownsUi)
            return DalamudUiTransactionResult.Fail("ContextActionOwnershipLost", $"The {option.SemanticName} UI transaction is not owned.");
        if (!RequiredOwnerIsVisible())
            return DalamudUiTransactionResult.Fail("RequiredUiOwnershipLost", $"The required {requiredVisibleAddon} UI closed during {option.SemanticName}.");
        var opened = contextMenuOpener.Advance(fingerprint);
        if (!opened.Success)
            return opened;
        return ResolveVisibleOption(fingerprint, submit: false);
    }

    public void Complete()
    {
        ownsUi = false;
        menuSelectionSubmitted = false;
        confirmationSubmitted = false;
        stableMenuMisses = 0;
        menuStability.Reset();
        contextMenuOpener.Reset();
    }

    public unsafe bool IsUiSettled() => !IsRawVisible("ContextMenu") && !IsRawVisible("SelectYesno");

    public unsafe void CloseOwnedUi()
    {
        if (!ownsUi)
            return;
        var menu = gameGui.GetAddonByName<AtkUnitBase>("ContextMenu", 1);
        if (menu != null && menu->IsVisible)
            menu->Close(true);
        var yesNo = gameGui.GetAddonByName<AddonSelectYesno>("SelectYesno", 1);
        if (yesNo != null && yesNo->AtkUnitBase.IsVisible && yesNo->NoButton != null && yesNo->NoButton->IsEnabled)
            yesNo->NoButton->ClickAddonButton(&yesNo->AtkUnitBase);
        if (IsUiSettled())
            Complete();
    }

    private unsafe DalamudUiTransactionResult SelectOption(EquipmentInstanceFingerprint fingerprint) =>
        ResolveVisibleOption(fingerprint, submit: true);

    private unsafe DalamudUiTransactionResult ResolveVisibleOption(EquipmentInstanceFingerprint fingerprint, bool submit)
    {
        if (!Enum.TryParse<InventoryType>(fingerprint.Container, out var inventoryType))
            return DalamudUiTransactionResult.Fail("UnsupportedContainer", $"Inventory container {fingerprint.Container} is not recognized.");
        var menu = gameGui.GetAddonByName<AtkUnitBase>("ContextMenu", 1);
        var agent = AgentInventoryContext.Instance();
        if (menu == null || !menu->IsReady || !menu->IsVisible || agent == null)
        {
            if (submit)
                menuStability.Observe(false);
            return DalamudUiTransactionResult.Pending("Waiting for the exact slot's context menu.");
        }
        if (agent->TargetInventoryId != inventoryType || agent->TargetInventorySlotId != fingerprint.SlotIndex)
        {
            if (submit)
                menuStability.Observe(false);
            return DalamudUiTransactionResult.Fail("UnexpectedContextMenu", "The visible context menu targets a different slot.");
        }
        var labels = ReadLabels(agent);
        var match = DalamudContextMenuOptionParser.Find(labels, option);
        if (!match.Success)
        {
            if (submit)
                menuStability.Observe(false);
            if (match.Code != "OptionAmbiguous" && ++stableMenuMisses < 8)
                return DalamudUiTransactionResult.Pending($"Waiting for stable context-menu labels ({labels.Count}/{agent->ContextItemCount} observed).");
            return DalamudUiTransactionResult.Fail(
                match.Code == "OptionAmbiguous" ? "ContextActionAmbiguous" : "ContextActionUnavailable",
                match.Code == "OptionAmbiguous" ? $"Multiple entries matched {option.SemanticName}." : $"The context menu does not offer {option.SemanticName}.");
        }
        stableMenuMisses = 0;
        if (match.Index >= agent->ContextItemCount || agent->IsContextItemDisabled(match.Index))
            return DalamudUiTransactionResult.Fail("ContextActionDisabled", $"The {option.SemanticName} entry is unavailable or disabled.");
        if (!submit)
            return DalamudUiTransactionResult.Completed("ContextActionProbePassed", $"The exact item's context menu offers enabled {option.SemanticName}.");
        if (!menuStability.Observe(true))
            return DalamudUiTransactionResult.Pending(
                $"Waiting for the enabled {option.SemanticName} entry to remain stable ({menuStability.ObservedConsecutiveFrames}/{menuStability.RequiredConsecutiveFrames} frames).");
        var values = stackalloc AtkValue[5];
        values[0] = new() { Type = AtkValueType.Int, Int = 0 };
        values[1] = new() { Type = AtkValueType.Int, Int = match.Index };
        values[2] = new() { Type = AtkValueType.Int, Int = 0 };
        values[3] = new() { Type = AtkValueType.Int, Int = 0 };
        values[4] = new() { Type = AtkValueType.Int, Int = 0 };
        if (!menu->FireCallback(5, values, true))
            return DalamudUiTransactionResult.Fail("ContextActionRejected", $"ContextMenu rejected {option.SemanticName}.");
        menuSelectionSubmitted = true;
        return DalamudUiTransactionResult.Pending($"Selected {option.SemanticName} and am waiting for the inventory transition.");
    }

    private unsafe bool RequiredOwnerIsVisible()
    {
        if (string.IsNullOrWhiteSpace(requiredVisibleAddon))
            return true;
        // Some owner addons have more than one registered instance while only
        // one is presented.  The owner is only a scope guard; the context menu
        // itself retains the exact-slot and semantic-option checks below.
        for (var index = 1; index <= 8; index++)
        {
            var addon = gameGui.GetAddonByName<AtkUnitBase>(requiredVisibleAddon, index);
            if (addon != null && addon->RootNode != null && addon->RootNode->IsVisible())
                return true;
        }
        return false;
    }

    private unsafe bool IsVisible(string name)
    {
        var addon = gameGui.GetAddonByName<AtkUnitBase>(name, 1);
        return addon != null && addon->IsReady && addon->IsVisible;
    }

    private unsafe bool IsRawVisible(string name)
    {
        var addon = gameGui.GetAddonByName<AtkUnitBase>(name, 1);
        return addon != null && addon->RootNode != null && addon->RootNode->IsVisible();
    }

    private static unsafe IReadOnlyList<string> ReadLabels(AgentInventoryContext* agent)
    {
        var labels = new List<string>();
        foreach (var parameter in agent->EventParams)
            if (parameter.Type is AtkValueType.String or AtkValueType.ManagedString or AtkValueType.WideString or AtkValueType.ConstString)
                labels.Add(parameter.GetValueAsString());
        return labels;
    }
}
