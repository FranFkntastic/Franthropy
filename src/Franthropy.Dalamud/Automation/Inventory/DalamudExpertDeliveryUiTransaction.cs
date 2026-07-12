using System.Runtime.InteropServices;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using ECommons.Automation.UIInput;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Franthropy.Dalamud.Equipment;

namespace Franthropy.Dalamud.Automation.Inventory;

public sealed class DalamudExpertDeliveryUiTransaction
{
    private readonly IGameGui gameGui;
    private readonly Func<string, bool> isExpectedSecondaryConfirmation;
    private bool ownsUi;
    private bool rowSubmitted;
    private bool rewardSubmitted;
    private bool secondarySubmitted;

    public DalamudExpertDeliveryUiTransaction(IGameGui gameGui, Func<string, bool> isExpectedSecondaryConfirmation)
    {
        this.gameGui = gameGui;
        this.isExpectedSecondaryConfirmation = isExpectedSecondaryConfirmation;
    }

    public unsafe DalamudUiTransactionResult Begin(EquipmentInstanceFingerprint fingerprint, uint currentSeals, uint maximumSeals)
    {
        if (ownsUi)
            return DalamudUiTransactionResult.Fail("ExpertDeliveryAlreadyOwned", "An Expert Delivery transaction is already active.");
        var list = gameGui.GetAddonByName<AtkUnitBase>("GrandCompanySupplyList", 1);
        if (list == null || !list->IsReady || !list->IsVisible ||
            list->UldManager.NodeListCount <= 24 || list->UldManager.SearchNodeById(24) == null ||
            !list->UldManager.SearchNodeById(24)->IsVisible())
            return DalamudUiTransactionResult.Fail("ExpertDeliveryListUnavailable", "Open the Grand Company Expert Delivery list before starting the run.");
        var entries = ReadEntries(list);
        var resolution = ExpertDeliverySelection.SelectExactItem(fingerprint.ItemId, entries, out var selected);
        if (!resolution.Success || selected is null)
            return resolution;
        if (maximumSeals <= currentSeals || selected.Seals > maximumSeals - currentSeals)
            return DalamudUiTransactionResult.Fail("ExpertDeliverySealCap", "This delivery would exceed the current company-seal capacity.");
        var values = stackalloc AtkValue[3];
        values[0] = new() { Type = AtkValueType.Int, Int = 1 };
        values[1] = new() { Type = AtkValueType.Int, Int = selected.Index };
        values[2] = new() { Type = AtkValueType.Int, Int = 0 };
        if (!list->FireCallback(3, values, true))
            return DalamudUiTransactionResult.Fail("ExpertDeliverySelectionRejected", "The visible Expert Delivery list rejected the selected row.");
        ownsUi = true;
        rowSubmitted = true;
        rewardSubmitted = false;
        secondarySubmitted = false;
        return DalamudUiTransactionResult.Completed("ExpertDeliveryRowSubmitted", "Selected the approved item through the visible Expert Delivery list.");
    }

    public unsafe DalamudUiTransactionResult Advance()
    {
        if (!ownsUi || !rowSubmitted)
            return DalamudUiTransactionResult.Fail("ExpertDeliveryUiOwnershipLost", "The Expert Delivery transaction does not own the current UI flow.");
        var reward = gameGui.GetAddonByName<AddonGrandCompanySupplyReward>("GrandCompanySupplyReward", 1);
        if (reward != null && reward->AtkUnitBase.IsReady && reward->AtkUnitBase.IsVisible)
        {
            if (rewardSubmitted)
                return DalamudUiTransactionResult.Pending("Waiting for the submitted Expert Delivery confirmation to complete.");
            if (reward->DeliverButton == null || !reward->DeliverButton->IsEnabled)
                return DalamudUiTransactionResult.Pending("Waiting for the visible Deliver button.");
            reward->DeliverButton->ClickAddonButton(&reward->AtkUnitBase);
            rewardSubmitted = true;
            return DalamudUiTransactionResult.Pending("Submitted the visible Expert Delivery confirmation.");
        }
        var yesNo = gameGui.GetAddonByName<AddonSelectYesno>("SelectYesno", 1);
        if (yesNo != null && yesNo->AtkUnitBase.IsReady && yesNo->AtkUnitBase.IsVisible)
        {
            if (secondarySubmitted)
                return DalamudUiTransactionResult.Pending("Waiting for the submitted high-quality confirmation to complete.");
            var observed = yesNo->PromptText->NodeText.ExtractText().Trim();
            if (!isExpectedSecondaryConfirmation(observed))
                return DalamudUiTransactionResult.Fail("UnexpectedExpertDeliveryPrompt", $"Expert Delivery opened an unexpected confirmation: {observed}");
            if (yesNo->YesButton == null || !yesNo->YesButton->IsEnabled)
                return DalamudUiTransactionResult.Pending("Waiting for the high-quality item confirmation.");
            yesNo->YesButton->ClickAddonButton(&yesNo->AtkUnitBase);
            secondarySubmitted = true;
            return DalamudUiTransactionResult.Pending("Confirmed the visible high-quality item warning.");
        }
        return DalamudUiTransactionResult.Pending("Waiting for Expert Delivery confirmation or inventory transition.");
    }

    public void Complete()
    {
        ownsUi = false;
        rowSubmitted = false;
        rewardSubmitted = false;
        secondarySubmitted = false;
    }

    public unsafe void CloseOwnedUi()
    {
        if (!ownsUi)
            return;
        var reward = gameGui.GetAddonByName<AtkUnitBase>("GrandCompanySupplyReward", 1);
        if (reward != null && reward->IsVisible)
            reward->Close(true);
        var yesNo = gameGui.GetAddonByName<AtkUnitBase>("SelectYesno", 1);
        if (yesNo != null && yesNo->IsVisible)
            yesNo->Close(true);
        Complete();
    }

    private static unsafe IReadOnlyList<ExpertDeliveryListEntry> ReadEntries(AtkUnitBase* addon)
    {
        if (addon->AtkValues == null || addon->AtkValuesCount <= 6 || addon->AtkValues[6].Type != AtkValueType.UInt)
            return [];
        var count = checked((int)addon->AtkValues[6].UInt);
        if (count is < 0 or > 500)
            return [];
        var pointer = *(nint*)((nint)addon + 648);
        if (pointer == 0)
            return [];
        var rows = (ExpertDeliveryEntry*)pointer;
        var result = new List<ExpertDeliveryListEntry>(count);
        for (var index = 0; index < count; index++)
            if (rows[index].ItemId != 0)
                result.Add(new ExpertDeliveryListEntry(rows[index].ItemId, rows[index].Seals, index));
        return result;
    }

    [StructLayout(LayoutKind.Explicit, Size = 152)]
    private struct ExpertDeliveryEntry
    {
        [FieldOffset(120)] public uint Seals;
        [FieldOffset(132)] public uint ItemId;
    }
}
