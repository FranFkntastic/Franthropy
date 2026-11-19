using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using ECommons.Automation;
using ECommons.Automation.UIInput;
using ECommons.GameHelpers;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using Franthropy.Dalamud.Automation.Ui;
using Franthropy.Dalamud.Diagnostics;

namespace Franthropy.Dalamud.Automation.Trades;

public interface ITradeIo
{
    ulong LocalContentId { get; }
    TradeOfferObservation? ObserveRemoteOffer();
    bool IsRemoteLocked { get; }
    bool TryGetExecutionReadiness(out string error);
    TradeInventoryObservation ObserveTradeableInventory();
    IReadOnlyList<TradePartner> GetAvailablePartners();
    bool TryGetSelectedPartner(out TradePartner partner);
    bool TryGetPartner(string name, string homeWorld, out TradePartner partner);
    bool PartnerIsAvailable(TradePartner partner);
    bool IsTradeOpen { get; }
    bool IsNumericInputOpen { get; }
    uint? ObserveTradePartnerEntityId();
    TradeOfferObservation? ObserveOffer();
    string ReadyDiagnostic { get; }
    bool CanClickReady { get; }
    bool CanConfirmTrade { get; }
    bool CanCancelTrade { get; }
    bool TryOpenTrade(TradePartner partner);
    bool TryOpenGilInput(out string error);
    bool TryOfferItem(TradeBatchLine line, out string error);
    bool TrySubmitQuantity(int quantity, out string error);
    bool TryClickReady(out string error);
    bool TryConfirmTrade(out string error);
    bool TryCancelTrade(out string error);
}

public sealed class DalamudTradeIo : ITradeIo, ITradeAutoAcceptIo
{
    private const string TradeAddon = "Trade";
    private const string NumericInputAddon = "InputNumeric";
    private const string SelectYesNoAddon = "SelectYesno";
    private const uint TradeInventoryContainerId = 2005;
    private const uint TradeConfirmationAddonRowId = 102223;
    // Trade.uld assigns these Addon rows to its two action buttons. Resolve
    // localized text, then locate the unique live control by semantic identity.
    private const uint TradeReadyAddonRowId = 203;
    private const uint TradeCancelAddonRowId = 204;
    private const string OfferItemTradeSignature =
        "48 89 6C 24 ?? 48 89 74 24 ?? 57 48 83 EC 30 83 B9 58 05 00 00 00 41 8B F0";

    internal static readonly InventoryType[] SupportedInventories =
    [
        InventoryType.Inventory1,
        InventoryType.Inventory2,
        InventoryType.Inventory3,
        InventoryType.Inventory4,
        InventoryType.Crystals,
    ];

    private readonly IGameGui gameGui;
    private readonly ITargetManager targetManager;
    private readonly IObjectTable objectTable;
    private readonly IPlayerState playerState;
    public ulong LocalContentId => playerState.IsLoaded && objectTable.LocalPlayer != null ? playerState.ContentId : 0;
    private readonly ICondition condition;
    private readonly ISigScanner sigScanner;
    private readonly IPluginLog log;
    private readonly HashSet<uint> tradeableItems;
    private readonly IReadOnlyDictionary<uint, string> itemNames;
    private readonly string tradeConfirmationText;
    private readonly string tradeReadyText;
    private readonly string tradeCancelText;
    private OfferItemTradeDelegate? offerItemTrade;
    private int tradeContextOffset;
    private string? lastCapabilityError;

    public DalamudTradeIo(
        IGameGui gameGui,
        ITargetManager targetManager,
        IObjectTable objectTable,
        IPlayerState playerState,
        ICondition condition,
        ISigScanner sigScanner,
        IDataManager dataManager,
        IPluginLog log)
    {
        this.gameGui = gameGui;
        this.targetManager = targetManager;
        this.objectTable = objectTable;
        this.playerState = playerState;
        this.condition = condition;
        this.sigScanner = sigScanner;
        this.log = log;
        var items = dataManager.GetExcelSheet<Item>()
            .Where(item => item.RowId > 0 && !item.IsUntradable)
            .ToList();
        tradeableItems = items.Select(item => item.RowId).ToHashSet();
        itemNames = items.ToDictionary(item => item.RowId, item => item.Name.ToString());
        tradeConfirmationText = dataManager.GetExcelSheet<Addon>()
            .GetRow(TradeConfirmationAddonRowId)
            .Text.ExtractText();
        tradeReadyText = dataManager.GetExcelSheet<Addon>().GetRow(TradeReadyAddonRowId).Text.ExtractText();
        tradeCancelText = dataManager.GetExcelSheet<Addon>().GetRow(TradeCancelAddonRowId).Text.ExtractText();
    }

    public bool IsTradeOpen => condition[ConditionFlag.TradeOpen];

    public bool TryGetExecutionReadiness(out string error) => TryResolveUiCapability(out error);

    public bool IsPartnerReadyForTrade =>
        IsTradeOpen && TradeDetectionManager.PartnerReadyForTrade;

    public unsafe bool CanClickReady => TryGetReadyButton(out _, out _);

    public unsafe string ReadyDiagnostic => ObserveReadyButton(out _, out _).Diagnostic;

    public unsafe uint? ObserveTradePartnerEntityId()
    {
        var manager = InventoryManager.Instance();
        var agent = AgentTrade.Instance();
        if (!IsTradeOpen || manager == null || agent == null || !agent->IsAgentActive() ||
            manager->TradePartnerEntityId == 0 || manager->TradePartnerEntityId == 0xE0000000)
            return null;
        return manager->TradePartnerEntityId;
    }

    public unsafe TradeOfferObservation? ObserveOffer()
    {
        var partnerEntityId = ObserveTradePartnerEntityId();
        if (partnerEntityId == null)
            return null;
        var manager = InventoryManager.Instance();
        var container = manager->GetInventoryContainer((InventoryType)TradeInventoryContainerId);
        return ReadOfferedInventory(container, partnerEntityId.Value, manager->TradeIsSyncPending,
            type => (nint)manager->GetInventoryContainer(type));
    }

    internal static unsafe TradeOfferObservation? ReadOfferedInventory(
        InventoryContainer* container, uint partnerEntityId, bool syncPending,
        Func<InventoryType, nint> sourceContainer)
    {
        if (container == null || !container->IsLoaded || container->Size < 6 || container->Items == null)
            return null;
        // Trade slots are symbolic links. GetInventorySlot resolves them to the
        // source stack, losing the offered quantity. SetTradeGilAmount writes the
        // local slot's Quantity directly; resolve only item identity and flags.
        var items = new List<TradeOfferedItem>();
        for (var index = 0; index < TradePlanner.MaximumTradeSlots; index++)
        {
            var item = container->Items + index;
            if (item->Quantity == 0)
                continue;
            if (item->Quantity < 0)
                return null;
            var source = item;
            if (item->IsSymbolic)
            {
                var type = (InventoryType)item->LinkedInventoryType;
                if (!SupportedInventories.Contains(type))
                    return null;
                var inventory = (InventoryContainer*)sourceContainer(type);
                if (inventory == null || !inventory->IsLoaded || inventory->Items == null ||
                    item->LinkedItemSlot >= inventory->Size)
                    return null;
                source = inventory->Items + item->LinkedItemSlot;
            }
            // A trade offer links directly to an ordinary carried stack. Never
            // invoke symbolic virtual getters: they can loop on the game thread.
            if (source->IsSymbolic || source->Quantity < item->Quantity)
                return null;
            var itemId = source->ItemId;
            if (itemId == 0)
                return null;
            items.Add(new(itemId, source->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality), checked((int)item->Quantity),
                item->IsSymbolic ? item->LinkedInventoryType : null,
                item->IsSymbolic ? item->LinkedItemSlot : null));
        }
        return container->Items[5].Quantity < 0 ? null
            : new(partnerEntityId, container->Items[5].Quantity, syncPending, items);
    }

    public unsafe bool CanConfirmTrade => TryGetTradeConfirmation(out _);

    public unsafe bool IsRemoteLocked
    {
        get
        {
            var manager = InventoryManager.Instance();
            return manager != null && manager->TradeRemoteState is
                TradeState.LockedIn or TradeState.WaitingForConfirmation or TradeState.Confirmed;
        }
    }

    public unsafe TradeOfferObservation? ObserveRemoteOffer()
    {
        if (ObserveTradePartnerEntityId() is not { } entity) return null;
        var manager = InventoryManager.Instance();
        var slots = manager->TradeItemsRemote;
        if (slots.Length != 6 || slots[5].Quantity < 0 || slots[5].IsSymbolic) return null;
        List<TradeOfferedItem> items = [];
        foreach (ref var slot in slots[..5])
        {
            if (slot.Quantity == 0) continue;
            if (slot.IsSymbolic || slot.ItemId == 0 || slot.Quantity < 0) return null;
            items.Add(new(slot.ItemId, slot.Flags.HasFlag(InventoryItem.ItemFlags.HighQuality), slot.Quantity));
        }
        return new(entity, slots[5].Quantity, manager->TradeIsSyncPending, items);
    }

    public unsafe bool CanCancelTrade => TryGetCancelButton(out _, out _);

    public unsafe bool IsNumericInputOpen
    {
        get
        {
            var addon = gameGui.GetAddonByName<AtkUnitBase>(NumericInputAddon, 1);
            return IsReady(addon);
        }
    }

    public unsafe TradeInventoryObservation ObserveTradeableInventory()
    {
        // Containers can remain allocated/loaded while logging out or in. They
        // cannot authorize destructive queue reconciliation without a character.
        if (objectTable.LocalPlayer == null)
            return TradeInventoryObservation.Unavailable;
        var inventoryManager = InventoryManager.Instance();
        if (inventoryManager == null)
            return TradeInventoryObservation.Unavailable;

        foreach (var inventoryType in SupportedInventories)
        {
            var container = inventoryManager->GetInventoryContainer(inventoryType);
            if (container == null || !container->IsLoaded)
                return TradeInventoryObservation.Unavailable;
        }

        var stacks = new List<TradeInventoryStack>();
        var gil = checked((int)inventoryManager->GetInventoryItemCount(
            TradePlanner.GilItemId,
            false,
            true,
            true,
            (short)0));
        if (gil > 0)
        {
            stacks.Add(new(
                uint.MaxValue,
                -1,
                TradePlanner.GilItemId,
                "Gil",
                false,
                gil));
        }

        foreach (var inventoryType in SupportedInventories)
        {
            var container = inventoryManager->GetInventoryContainer(inventoryType);

            for (var slotIndex = 0; slotIndex < container->Size; slotIndex++)
            {
                var slot = container->GetInventorySlot(slotIndex);
                if (slot == null ||
                    slot->ItemId == 0 ||
                    slot->Quantity <= 0 ||
                    !tradeableItems.Contains(slot->ItemId) ||
                    slot->SpiritbondOrCollectability != 0 ||
                    slot->GlamourId != 0)
                {
                    continue;
                }

                var name = itemNames.GetValueOrDefault(slot->ItemId, $"Item {slot->ItemId}");
                stacks.Add(new(
                    (uint)inventoryType,
                    slotIndex,
                    slot->ItemId,
                    name,
                    slot->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality),
                    checked((int)slot->Quantity)));
            }
        }

        return TradeInventoryObservation.Authoritative(stacks);
    }

    public bool TryGetSelectedPartner(out TradePartner partner)
    {
        var selected = targetManager.Target as IPlayerCharacter;
        var focused = targetManager.FocusTarget as IPlayerCharacter;
        var player = selected is { IsTargetable: true }
            ? selected
            : focused is { IsTargetable: true } ? focused : null;
        if (player == null)
        {
            partner = new(0, string.Empty, 0);
            return false;
        }

        partner = CreatePartner(player);
        return true;
    }

    public IReadOnlyList<TradePartner> GetAvailablePartners() =>
        EnumeratePartnerCandidates()
            .Where(player => player.IsTargetable)
            .GroupBy(player => player.GameObjectId)
            .Select(group => CreatePartner(group.First()))
            .OrderBy(candidate => candidate.Name, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.HomeWorldName, StringComparer.Ordinal)
            .ToArray();

    public bool TryGetPartner(string name, string homeWorld, out TradePartner partner)
    {
        var player = EnumeratePartnerCandidates()
            .FirstOrDefault(candidate =>
                candidate.IsTargetable &&
                string.Equals(candidate.Name.TextValue, name, StringComparison.Ordinal) &&
                string.Equals(ResolveHomeWorldName(candidate), homeWorld, StringComparison.Ordinal));
        if (player == null)
        {
            partner = new(0, string.Empty, 0);
            return false;
        }

        partner = CreatePartner(player);
        return true;
    }

    public bool PartnerIsAvailable(TradePartner partner) =>
        TryResolvePartner(partner, out _);

    public bool TryOpenTrade(TradePartner partner)
    {
        if (!TryResolvePartner(partner, out var player))
            return false;

        if (targetManager.Target?.GameObjectId != player.GameObjectId)
        {
            targetManager.Target = player;
            return false;
        }

        Chat.SendMessage("/trade");
        return true;
    }

    private bool TryResolvePartner(TradePartner partner, out IPlayerCharacter player)
    {
        player = EnumeratePartnerCandidates()
            .FirstOrDefault(candidate =>
                candidate.IsTargetable &&
                candidate.GameObjectId == partner.GameObjectId &&
                candidate.Name.TextValue == partner.Name &&
                candidate.HomeWorld.RowId == partner.HomeWorldId)!;
        return player != null;
    }

    private IEnumerable<IPlayerCharacter> EnumeratePartnerCandidates()
    {
        var selected = targetManager.Target as IPlayerCharacter;
        if (selected != null)
            yield return selected;
        var focused = targetManager.FocusTarget as IPlayerCharacter;
        if (focused != null &&
            focused.GameObjectId != selected?.GameObjectId)
        {
            yield return focused;
        }

        foreach (var player in objectTable.OfType<IPlayerCharacter>())
        {
            if (player.GameObjectId != selected?.GameObjectId &&
                player.GameObjectId != focused?.GameObjectId)
            {
                yield return player;
            }
        }
    }

    public unsafe bool TryOpenGilInput(out string error)
    {
        if (!TryResolveUiCapability(out error))
            return false;

        var addon = gameGui.GetAddonByName<AtkUnitBase>(TradeAddon, 1);
        if (!IsReady(addon))
            return false;

        try
        {
            Callback.Fire(addon, true, 2, Callback.ZeroAtkValue);
            return true;
        }
        catch (Exception exception)
        {
            log.Error(exception, "[Franthropy] Unable to open the trade gil input.");
            error = $"The trade-gil command is unavailable: {exception.Message}";
            return false;
        }
    }

    public unsafe bool TryOfferItem(TradeBatchLine line, out string error)
    {
        if (!TryResolveItemCapability(out error))
            return false;

        var inventoryManager = InventoryManager.Instance();
        if (inventoryManager == null)
        {
            return false;
        }

        var inventoryType = (InventoryType)line.ContainerId;
        var container = inventoryManager->GetInventoryContainer(inventoryType);
        if (container == null || !container->IsLoaded || line.SlotIndex < 0 || line.SlotIndex >= container->Size)
        {
            return false;
        }

        var slot = container->GetInventorySlot(line.SlotIndex);
        if (slot == null ||
            slot->ItemId != line.ItemId ||
            slot->Quantity != line.SourceStackQuantity ||
            slot->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality) != line.IsHighQuality)
        {
            return false;
        }

        var agent = AgentTrade.Instance();
        if (agent == null || !agent->IsAgentActive())
        {
            return false;
        }

        try
        {
            offerItemTrade!((nint)agent + tradeContextOffset, checked((ushort)line.SlotIndex), inventoryType);
            return true;
        }
        catch (Exception exception)
        {
            log.Error(exception, "[Franthropy] Unable to offer an inventory item to trade.");
            error = $"The trade-item command is unavailable: {exception.Message}";
            return false;
        }
    }

    public unsafe bool TrySubmitQuantity(int quantity, out string error)
    {
        if (!TryResolveUiCapability(out error))
            return false;

        var addon = gameGui.GetAddonByName<AtkUnitBase>(NumericInputAddon, 1);
        if (quantity <= 0)
        {
            error = $"Requested trade quantity {quantity:N0} is invalid.";
            return false;
        }
        // The source stack can change between native dispatch and dialog load.
        // Never clamp the user's intent; wait, then let the runner rebuild the
        // uncommitted offer from fresh inventory if the smaller limit persists.
        return DalamudNumericDialog.TrySubmit(addon, quantity) == NumericDialogSubmission.Submitted;
    }

    public unsafe bool TryClickReady(out string error)
    {
        if (!TryResolveUiCapability(out error))
            return false;

        var observation = ObserveReadyButton(out var addon, out var button);
        if (observation.State != TradeButtonState.Ready)
        {
            // Transient readiness is not a capability failure. The runner owns a bounded wait.
            error = observation.State == TradeButtonState.Ambiguous ? observation.Diagnostic : string.Empty;
            return false;
        }

        button->ClickAddonButton(addon);
        return true;
    }

    public unsafe bool TryConfirmTrade(out string error)
    {
        if (!TryResolveUiCapability(out error))
            return false;

        if (!TryGetTradeConfirmation(out var addon))
            return false;

        new AddonMaster.SelectYesno(addon).Yes();
        return true;
    }

    public unsafe bool TryCancelTrade(out string error)
    {
        if (!TryResolveUiCapability(out error))
            return false;

        var manager = InventoryManager.Instance();
        if (manager == null || manager->TradeLocalState != TradeState.SelectingTradeGoods)
        {
            error = "Local trade is not observably pre-lock; leaving it untouched.";
            return false;
        }
        if (!TryGetCancelButton(out var addon, out var button))
        {
            error = "The trade Cancel control is not an enabled Button with the expected label.";
            return false;
        }

        button->ClickAddonButton(addon);
        return true;
    }

    private unsafe bool TryGetReadyButton(
        out AtkUnitBase* addon,
        out AtkComponentButton* button) => ObserveReadyButton(out addon, out button).State == TradeButtonState.Ready;

    private unsafe TradeButtonObservation ObserveReadyButton(out AtkUnitBase* addon, out AtkComponentButton* button)
        => ObserveTradeButton(tradeReadyText, out addon, out button);

    private unsafe bool TryGetTradeConfirmation(out AddonSelectYesno* addon)
    {
        addon = null;
        for (var index = 1; index < 100; index++)
        {
            var candidate = gameGui.GetAddonByName<AddonSelectYesno>(SelectYesNoAddon, index);
            if (candidate == null)
                return false;
            if (!IsReady(&candidate->AtkUnitBase))
                continue;

            if (candidate->PromptText == null)
                continue;
            var prompt = candidate->PromptText->NodeText.ExtractText();
            if (string.Equals(prompt, tradeConfirmationText, StringComparison.Ordinal))
            {
                addon = candidate;
                return true;
            }
        }

        return false;
    }

    private unsafe bool TryGetCancelButton(
        out AtkUnitBase* addon,
        out AtkComponentButton* button) => TryGetTradeButton(tradeCancelText, out addon, out button);

    private unsafe bool TryGetTradeButton(string label, out AtkUnitBase* addon, out AtkComponentButton* button)
        => ObserveTradeButton(label, out addon, out button).State == TradeButtonState.Ready;

    private unsafe TradeButtonObservation ObserveTradeButton(string label, out AtkUnitBase* addon, out AtkComponentButton* button)
    {
        addon = gameGui.GetAddonByName<AtkUnitBase>(TradeAddon, 1);
        button = null;
        if (!IsReady(addon) || addon->UldManager.NodeList == null)
            return new(TradeButtonState.Loading, 0, "Waiting for the trade controls to load.");

        var candidates = new List<TradeButtonCandidate>();
        for (var index = 0; index < addon->UldManager.NodeListCount; index++)
        {
            var node = addon->UldManager.NodeList[index];
            if (node == null)
                continue;
            var candidate = node->GetAsAtkComponentButton();
            if (candidate == null || candidate->ButtonTextNode == null)
                continue;
            candidates.Add(new((nint)candidate, candidate->ButtonTextNode->NodeText.ExtractText(),
                node->IsVisible(), candidate->IsEnabled));
        }
        var observation = TradeButtonIdentity.Observe(candidates, label);
        button = (AtkComponentButton*)observation.Address;
        return observation;
    }

    private static TradePartner CreatePartner(IPlayerCharacter player) =>
        new(
            player.GameObjectId,
            player.Name.TextValue,
            player.HomeWorld.RowId,
            ResolveHomeWorldName(player));

    private static string ResolveHomeWorldName(IPlayerCharacter player) =>
        player.HomeWorld.IsValid
            ? player.HomeWorld.Value.Name.ToString()
            : string.Empty;

    private static unsafe bool IsReady(AtkUnitBase* addon) =>
        addon != null && addon->IsReady && addon->IsVisible;

    private static bool TryResolveUiCapability(out string error)
    {
        try
        {
            NativeCapabilityGuard.RequireAddress(AtkResNode.Addresses.GetAsAtkComponentButton.Value, "Trade Button component resolver");
            NativeCapabilityGuard.RequireAddress(AtkUnitBase.Addresses.FireCallback.Value, "Trade UI callbacks");
            error = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            error = $"Trade UI is unavailable: {exception.Message}";
            return false;
        }
    }

    private bool TryResolveItemCapability(out string error)
    {
        try
        {
            if (offerItemTrade == null)
            {
                var contextOffset = checked((int)Marshal.OffsetOf<AgentTrade>(nameof(AgentTrade.InventoryContextEvent)));
                RequireTradeContextLayout(Marshal.SizeOf<AgentTrade>(), contextOffset,
                    Marshal.SizeOf<AgentInventoryContext.InventoryContextEvent>());
                offerItemTrade = Marshal.GetDelegateForFunctionPointer<OfferItemTradeDelegate>(
                    NativeCapabilityGuard.ResolveUnique(sigScanner, OfferItemTradeSignature, "Trade item offering"));
                tradeContextOffset = contextOffset;
            }
            lastCapabilityError = null;
            error = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            error = $"Trade item offering is unavailable: {exception.Message}";
            if (!string.Equals(lastCapabilityError, error, StringComparison.Ordinal))
                log.Warning("[Franthropy] {Message}", error);
            lastCapabilityError = error;
            return false;
        }
    }

    internal static void RequireTradeContextLayout(int agentSize, int contextOffset, int contextSize)
    {
        // ClientStructs exposes the InventoryContextEvent base, while the trade
        // specialization extends it inside AgentTrade. Validate the enclosing
        // allocation through +0x55C; the signature binds the +0x558 access.
        if (contextOffset < 0 || contextSize < IntPtr.Size ||
            agentSize - contextOffset < Math.Max(contextSize, 0x560))
            throw new InvalidOperationException("AgentTrade.InventoryContextEvent does not contain the native offer-item fields; the trade layout is unsupported.");
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void OfferItemTradeDelegate(nint tradeAddress, ushort slot, InventoryType inventoryType);
}
