using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using ECommons.Automation;
using ECommons.Automation.UIInput;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using ValueType = FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType;

namespace Franthropy.Dalamud.Automation.Vendors;

public sealed class DalamudGilVendorAccessReader
{
    private readonly IClientState clientState;
    private readonly IPlayerState playerState;
    private readonly IObjectTable objectTable;

    public DalamudGilVendorAccessReader(
        IClientState clientState,
        IPlayerState playerState,
        IObjectTable objectTable)
    {
        this.clientState = clientState;
        this.playerState = playerState;
        this.objectTable = objectTable;
    }

    public GilVendorAccessAssessment Assess(GilVendorOffer offer)
    {
        ArgumentNullException.ThrowIfNull(offer);
        if (!playerState.IsLoaded)
            return new(GilVendorAccessState.Unknown, "PlayerStateUnavailable", "Character access state is not loaded.");

        if (clientState.TerritoryType == offer.TerritoryId)
        {
            return FindLiveNpc(offer) is not null
                ? new(GilVendorAccessState.Verified, "NpcVisible", "The expected vendor is targetable in the current territory.")
                : new(GilVendorAccessState.Probeable, "CurrentTerritory", "The vendor is in the current territory and will be verified before spending.");
        }

        if (!TryReadAttunedAetherytes(out var attuned))
            return new(GilVendorAccessState.Unknown, "TeleportListUnavailable", "The character's live teleport destinations are unavailable.");
        var route = offer.RouteAetheryteIds.FirstOrDefault(attuned.Contains);
        return route == 0
            ? new(GilVendorAccessState.Unavailable, "NoAttunedRoute", "No attuned destination reaches this vendor territory.")
            : new(GilVendorAccessState.Probeable, "AttunedRoute", "An attuned destination can reach this vendor.", route);
    }

    public IGameObject? FindLiveNpc(GilVendorOffer offer)
    {
        ArgumentNullException.ThrowIfNull(offer);
        return objectTable
            .Where(obj =>
                obj.ObjectKind == ObjectKind.EventNpc &&
                obj.BaseId == offer.NpcId &&
                obj.IsTargetable)
            .OrderBy(obj => System.Numerics.Vector3.DistanceSquared(obj.Position, offer.Position))
            .FirstOrDefault();
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
}

public sealed class DalamudOrdinaryGilShop
{
    private const string ShopAddon = "Shop";
    private readonly IGameGui gameGui;

    public DalamudOrdinaryGilShop(IGameGui gameGui)
    {
        this.gameGui = gameGui;
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
}
