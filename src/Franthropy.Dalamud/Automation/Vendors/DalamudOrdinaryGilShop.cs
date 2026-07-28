using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using ECommons.Automation;
using ECommons.Automation.UIInput;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Franthropy.Dalamud.Automation.Retainers;
using Lumina.Excel.Sheets;
using ValueType = FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType;

namespace Franthropy.Dalamud.Automation.Vendors;

public sealed class DalamudGilVendorAccessReader
{
    private static readonly TimeSpan AssessmentLifetime = TimeSpan.FromSeconds(1);
    private const float MaximumLocationDistanceSquared = 30f * 30f;
    private readonly IClientState clientState;
    private readonly IPlayerState playerState;
    private readonly IObjectTable objectTable;
    private readonly Func<DateTimeOffset> utcNow;
    private readonly Dictionary<(uint NpcId, uint ShopId, uint TerritoryId), CachedAssessment> assessments = [];
    private IReadOnlySet<uint>? cachedAetherytes;
    private DateTimeOffset aetherytesObservedAt;
    private uint cachedTerritory;
    private ulong cachedOwner;

    public DalamudGilVendorAccessReader(
        IClientState clientState,
        IPlayerState playerState,
        IObjectTable objectTable,
        Func<DateTimeOffset>? utcNow = null)
    {
        this.clientState = clientState;
        this.playerState = playerState;
        this.objectTable = objectTable;
        this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public GilVendorAccessAssessment Assess(GilVendorOffer offer)
    {
        ArgumentNullException.ThrowIfNull(offer);
        if (cachedTerritory != clientState.TerritoryType ||
            cachedOwner != playerState.ContentId)
        {
            cachedTerritory = clientState.TerritoryType;
            cachedOwner = playerState.ContentId;
            assessments.Clear();
            cachedAetherytes = null;
        }
        var key = (offer.NpcId, offer.ShopId, offer.TerritoryId);
        if (assessments.TryGetValue(key, out var cached) &&
            utcNow() - cached.ObservedAt < AssessmentLifetime)
        {
            return cached.Assessment;
        }

        var assessment = AssessCore(offer);
        assessments[key] = new(utcNow(), assessment);
        return assessment;
    }

    private GilVendorAccessAssessment AssessCore(GilVendorOffer offer)
    {
        if (!playerState.IsLoaded)
            return new(GilVendorAccessState.Unknown, "PlayerStateUnavailable", "Character access state is not loaded.");

        if (clientState.TerritoryType == offer.TerritoryId)
        {
            return FindLiveNpc(offer) is not null
                ? new(GilVendorAccessState.Verified, "NpcVisible", "The expected vendor is targetable in the current territory.")
                : new(GilVendorAccessState.Probeable, "CurrentTerritory", "The vendor is in the current territory and will be verified before spending.");
        }

        if (!TryReadCachedAttunedAetherytes(out var attuned))
            return new(GilVendorAccessState.Unknown, "TeleportListUnavailable", "The character's live teleport destinations are unavailable.");
        var route = offer.RouteAetheryteIds.FirstOrDefault(attuned.Contains);
        return route == 0
            ? new(GilVendorAccessState.Unavailable, "NoAttunedRoute", "No attuned destination reaches this vendor territory.")
            : new(GilVendorAccessState.Probeable, "AttunedRoute", "An attuned destination can reach this vendor.", route);
    }

    private bool TryReadCachedAttunedAetherytes(out IReadOnlySet<uint> aetherytes)
    {
        if (cachedAetherytes is not null &&
            utcNow() - aetherytesObservedAt < AssessmentLifetime)
        {
            aetherytes = cachedAetherytes;
            return true;
        }
        if (!TryReadAttunedAetherytes(out aetherytes))
            return false;
        cachedAetherytes = aetherytes;
        aetherytesObservedAt = utcNow();
        return true;
    }

    public IGameObject? FindLiveNpc(GilVendorOffer offer)
    {
        ArgumentNullException.ThrowIfNull(offer);
        var matches = objectTable
            .Where(obj =>
                obj.ObjectKind == ObjectKind.EventNpc &&
                obj.BaseId == offer.NpcId &&
                obj.IsTargetable &&
                System.Numerics.Vector3.DistanceSquared(obj.Position, offer.Position) <= MaximumLocationDistanceSquared)
            .ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    public unsafe bool TryTeleport(uint aetheryteId)
    {
        if (aetheryteId == 0 || !TryReadAttunedAetherytes(out var attuned) || !attuned.Contains(aetheryteId))
            return false;
        var telepo = Telepo.Instance();
        return telepo != null && telepo->Teleport(aetheryteId, 0);
    }

    public static unsafe bool TryReadAttunedAetherytes(out IReadOnlySet<uint> aetherytes)
    {
        var telepo = Telepo.Instance();
        if (telepo == null)
        {
            aetherytes = new HashSet<uint>();
            return false;
        }

        telepo->UpdateAetheryteList();
        var result = new HashSet<uint>();
        for (var index = 0; index < telepo->TeleportList.Count; index++)
            result.Add(telepo->TeleportList[index].AetheryteId);
        aetherytes = result;
        return true;
    }

    private sealed record CachedAssessment(
        DateTimeOffset ObservedAt,
        GilVendorAccessAssessment Assessment);
}

public sealed class DalamudOrdinaryGilShop
{
    private const string ShopAddon = "Shop";
    private static readonly IReadOnlySet<string> EmptyMenuEntries = new HashSet<string>();
    private readonly IGameGui gameGui;
    private readonly IReadOnlyDictionary<uint, IReadOnlySet<string>> menuEntriesByShopId;

    public DalamudOrdinaryGilShop(IGameGui gameGui)
    {
        this.gameGui = gameGui ?? throw new ArgumentNullException(nameof(gameGui));
        menuEntriesByShopId = new Dictionary<uint, IReadOnlySet<string>>();
    }

    public DalamudOrdinaryGilShop(IGameGui gameGui, IDataManager dataManager)
    {
        this.gameGui = gameGui ?? throw new ArgumentNullException(nameof(gameGui));
        ArgumentNullException.ThrowIfNull(dataManager);
        menuEntriesByShopId = BuildMenuEntries(dataManager);
    }

    public unsafe bool IsOpen
    {
        get
        {
            var addon = gameGui.GetAddonByName<AtkUnitBase>(ShopAddon, 1);
            return addon != null && addon->IsReady && addon->IsVisible;
        }
    }

    public unsafe GilVendorShopReadResult ReadRows()
    {
        var addon = gameGui.GetAddonByName<AtkUnitBase>(ShopAddon, 1);
        if (addon == null || !addon->IsReady || !addon->IsVisible)
            return GilVendorShopReadResult.Fail("ShopNotOpen", "No ready ordinary-gil shop is open.");
        if (!TryReadUInt(addon, 2, out var count) || count > 500)
            return GilVendorShopReadResult.Fail("InvalidShopShape", "The open shop did not expose a bounded row count.");

        var rows = new List<GilVendorShopRow>((int)count);
        for (var index = 0; index < count; index++)
        {
            if (!TryReadUInt(addon, checked(441 + (int)index), out var itemId) ||
                !TryReadUInt(addon, checked(75 + (int)index), out var price) ||
                itemId == 0 ||
                price == 0)
            {
                return GilVendorShopReadResult.Fail("InvalidShopRow", $"Shop row {index} did not expose an item and unit gil price.");
            }

            rows.Add(new((int)index, itemId, price));
        }

        return GilVendorShopReadResult.Success(rows);
    }

    public unsafe GilVendorMenuAdvanceResult TryAdvanceOfferMenu(GilVendorOffer offer)
    {
        ArgumentNullException.ThrowIfNull(offer);
        if (!menuEntriesByShopId.TryGetValue(offer.ShopId, out var targets))
            targets = EmptyMenuEntries;

        var selectString = TrySelectRenderedMenuEntry("SelectString", entryStride: 1, targets);
        if (selectString.MenuPresented)
            return selectString;
        return TrySelectRenderedMenuEntry("SelectIconString", entryStride: 3, targets);
    }

    public unsafe bool TrySubmitPurchase(GilVendorShopRow row, uint quantity, out string error)
    {
        if (quantity is 0 or > 99)
        {
            error = "Ordinary-gil purchase batches must contain 1 through 99 items.";
            return false;
        }

        var addon = gameGui.GetAddonByName<AtkUnitBase>(ShopAddon, 1);
        if (addon == null || !addon->IsReady || !addon->IsVisible)
        {
            error = "The ordinary-gil shop closed before purchase submission.";
            return false;
        }

        Callback.Fire(addon, true, 0, (uint)row.RowIndex, quantity, 0);
        error = string.Empty;
        return true;
    }

    public unsafe bool TryConfirmOwnedPrompt()
    {
        var addon = gameGui.GetAddonByName<AddonSelectYesno>("SelectYesno", 1);
        if (addon == null ||
            !addon->AtkUnitBase.IsReady ||
            !addon->AtkUnitBase.IsVisible ||
            addon->YesButton == null ||
            !addon->YesButton->IsEnabled)
        {
            return false;
        }

        addon->YesButton->ClickAddonButton(&addon->AtkUnitBase);
        return true;
    }

    public unsafe void Close()
    {
        var addon = gameGui.GetAddonByName<AtkUnitBase>(ShopAddon, 1);
        if (addon != null && addon->IsVisible)
            addon->Close(true);
    }

    private static unsafe bool TryReadUInt(AtkUnitBase* addon, int index, out uint value)
    {
        value = 0;
        if (index < 0 || index >= addon->AtkValuesCount)
            return false;
        var atkValue = addon->AtkValues[index];
        switch (atkValue.Type)
        {
            case ValueType.UInt:
                value = atkValue.UInt;
                return true;
            case ValueType.Int when atkValue.Int >= 0:
                value = (uint)atkValue.Int;
                return true;
            default:
                return false;
        }
    }

    private unsafe GilVendorMenuAdvanceResult TrySelectRenderedMenuEntry(
        string addonName,
        int entryStride,
        IReadOnlySet<string> targets)
    {
        var addon = gameGui.GetAddonByName<AtkUnitBase>(addonName, 1);
        if (!IsPresented(addon) ||
            addon->AtkValues == null ||
            addon->AtkValuesCount <= 7 ||
            addon->AtkValues[5].Type != ValueType.Int)
        {
            return GilVendorMenuAdvanceResult.NotPresented();
        }

        var entryCount = Math.Max(0, addon->AtkValues[5].Int);
        for (var index = 0; index < entryCount; index++)
        {
            var valueIndex = 7 + (index * entryStride);
            if (valueIndex >= addon->AtkValuesCount)
                break;
            var value = addon->AtkValues + valueIndex;
            if (value->Type is not (ValueType.String or ValueType.ManagedString or ValueType.WideString or ValueType.ConstString))
                continue;
            var observed = value->GetValueAsString().Trim();
            if (!targets.Any(target => RetainerUiAutomationText.IsSelectStringEntryMatch(observed, target)))
                continue;
            addon->FireCallbackInt(index);
            return GilVendorMenuAdvanceResult.Selected(observed);
        }
        return GilVendorMenuAdvanceResult.NoMatchingEntry();
    }

    private static IReadOnlyDictionary<uint, IReadOnlySet<string>> BuildMenuEntries(IDataManager dataManager)
    {
        var entries = dataManager.GetExcelSheet<GilShop>()
            .Where(row => row.RowId != 0)
            .ToDictionary(
                row => row.RowId,
                row => new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        foreach (var shop in dataManager.GetExcelSheet<GilShop>())
            AddMenuEntry(entries, shop.RowId, shop.Name.ExtractText().Trim());
        foreach (var topic in dataManager.GetExcelSheet<TopicSelect>())
        {
            var topicName = topic.Name.ExtractText().Trim();
            foreach (var shop in topic.Shop.Where(value => value.Is<GilShop>()))
                AddMenuEntry(entries, shop.RowId, topicName);
        }
        return entries.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlySet<string>)pair.Value);
    }

    private static void AddMenuEntry(
        IReadOnlyDictionary<uint, HashSet<string>> entries,
        uint shopId,
        string entry)
    {
        if (!string.IsNullOrWhiteSpace(entry) && entries.TryGetValue(shopId, out var names))
            names.Add(entry);
    }

    private static unsafe bool IsPresented(AtkUnitBase* addon) =>
        addon != null && addon->RootNode != null && addon->RootNode->IsVisible();
}
