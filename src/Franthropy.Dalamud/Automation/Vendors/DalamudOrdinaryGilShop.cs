using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using ECommons.Automation.UIInput;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Franthropy.Dalamud.Automation.Retainers;
using Lumina.Excel.Sheets;
using ValueType = FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType;

namespace Franthropy.Dalamud.Automation.Vendors;

public sealed class DalamudGilVendorAccessReader
{
    private static readonly TimeSpan AssessmentLifetime = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan AetheryteRefreshLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan FailedAetheryteRefreshBackoff = TimeSpan.FromSeconds(2);
    private const float MaximumLocationDistanceSquared = 30f * 30f;
    private readonly IClientState clientState;
    private readonly IPlayerState playerState;
    private readonly IObjectTable objectTable;
    private readonly IAetheryteList aetheryteList;
    private readonly Func<DateTimeOffset> utcNow;
    private readonly Func<uint, bool?> isQuestComplete;
    private readonly Dictionary<(uint NpcId, uint ShopId, uint TerritoryId), CachedAssessment> assessments = [];
    private readonly GilVendorAetheryteSnapshot aetheryteSnapshot = new();
    private DateTimeOffset nextAetheryteRefreshAt;
    private string? aetheryteRefreshFailure;
    private uint cachedTerritory;
    private ulong cachedOwner;

    public DalamudGilVendorAccessReader(
        IClientState clientState,
        IPlayerState playerState,
        IObjectTable objectTable,
        IAetheryteList aetheryteList,
        Func<DateTimeOffset>? utcNow = null,
        Func<uint, bool?>? isQuestComplete = null)
    {
        this.clientState = clientState;
        this.playerState = playerState;
        this.objectTable = objectTable;
        this.aetheryteList = aetheryteList;
        this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        this.isQuestComplete = isQuestComplete ?? ReadQuestCompletion;
    }

    public GilVendorAccessAssessment Assess(GilVendorOffer offer)
    {
        ArgumentNullException.ThrowIfNull(offer);
        SynchronizeOwnerAndTerritory();
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

    /// <summary>
    /// Captures the owner-scoped teleport list from Dalamud outside the render path.
    /// Call this from a framework update; <see cref="Assess"/> only reads the managed snapshot.
    /// </summary>
    public void RefreshAttunedAetherytes()
    {
        SynchronizeOwnerAndTerritory();
        var owner = playerState.ContentId;
        if (!playerState.IsLoaded ||
            owner == 0 ||
            objectTable.LocalPlayer is null ||
            utcNow() < nextAetheryteRefreshAt)
        {
            return;
        }

        try
        {
            var count = aetheryteList.Length;
            var observed = new HashSet<uint>();
            for (var index = 0; index < count; index++)
            {
                if (objectTable.LocalPlayer is null || playerState.ContentId != owner)
                    return;
                if (aetheryteList[index] is { } entry && entry.AetheryteId != 0)
                    observed.Add(entry.AetheryteId);
            }

            if (objectTable.LocalPlayer is null || playerState.ContentId != owner)
                return;
            aetheryteSnapshot.Observe(owner, observed);
            aetheryteRefreshFailure = null;
            nextAetheryteRefreshAt = utcNow().Add(AetheryteRefreshLifetime);
            assessments.Clear();
        }
        catch (Exception ex)
        {
            aetheryteRefreshFailure = ex.GetType().Name;
            nextAetheryteRefreshAt = utcNow().Add(FailedAetheryteRefreshBackoff);
        }
    }

    private GilVendorAccessAssessment AssessCore(GilVendorOffer offer)
    {
        if (!playerState.IsLoaded)
            return new(GilVendorAccessState.Unknown, "PlayerStateUnavailable", "Character access state is not loaded.");

        var unlockAssessment = AssessShopUnlocks(offer, isQuestComplete);
        if (unlockAssessment is not null)
            return unlockAssessment;

        if (clientState.TerritoryType == offer.TerritoryId)
        {
            return FindLiveNpc(offer) is not null
                ? new(GilVendorAccessState.Verified, "NpcVisible", "The expected vendor is targetable in the current territory.")
                : new(GilVendorAccessState.Probeable, "CurrentTerritory", "The vendor is in the current territory and will be verified before spending.");
        }

        if (!aetheryteSnapshot.TryRead(playerState.ContentId, out var attuned))
        {
            var detail = string.IsNullOrWhiteSpace(aetheryteRefreshFailure)
                ? "The character's teleport destinations are still being observed."
                : $"The character's teleport destinations could not be observed ({aetheryteRefreshFailure}).";
            return new(GilVendorAccessState.Unknown, "TeleportListUnavailable", detail);
        }
        var route = offer.EffectiveTravelRoutes.FirstOrDefault(candidate => attuned.Contains(candidate.AetheryteId));
        return route is null
            ? new(GilVendorAccessState.Unavailable, "NoAttunedRoute", "No attuned destination reaches this vendor territory.")
            : new(
                GilVendorAccessState.Probeable,
                "AttunedRoute",
                "An attuned destination can reach this vendor.",
                route.AetheryteId,
                route.AethernetId,
                route.AetheryteTerritoryId);
    }

    internal static GilVendorAccessAssessment? AssessShopUnlocks(
        GilVendorOffer offer,
        Func<uint, bool?> isQuestComplete)
    {
        ArgumentNullException.ThrowIfNull(offer);
        ArgumentNullException.ThrowIfNull(isQuestComplete);
        foreach (var questId in offer.RequiredQuestIds.Distinct())
        {
            bool? completed;
            try
            {
                completed = isQuestComplete(questId);
            }
            catch (Exception ex)
            {
                return new(
                    GilVendorAccessState.Unknown,
                    "ShopUnlockStateUnavailable",
                    $"Could not verify whether {offer.NpcName}'s shop is unlocked ({ex.GetType().Name}).");
            }

            if (completed is null)
            {
                return new(
                    GilVendorAccessState.Unknown,
                    "ShopUnlockStateUnavailable",
                    $"Could not verify whether {offer.NpcName}'s shop is unlocked.");
            }
            if (!completed.Value)
            {
                return new(
                    GilVendorAccessState.Unavailable,
                    "ShopLocked",
                    $"{offer.NpcName}'s shop is not unlocked for this character.");
            }
        }

        return null;
    }

    private static bool? ReadQuestCompletion(uint questId) => QuestManager.IsQuestComplete(questId);

    private void SynchronizeOwnerAndTerritory()
    {
        var owner = playerState.ContentId;
        if (cachedOwner != owner)
        {
            cachedOwner = owner;
            aetheryteSnapshot.SynchronizeOwner(owner);
            aetheryteRefreshFailure = null;
            nextAetheryteRefreshAt = DateTimeOffset.MinValue;
            assessments.Clear();
        }

        if (cachedTerritory == clientState.TerritoryType)
            return;
        cachedTerritory = clientState.TerritoryType;
        nextAetheryteRefreshAt = DateTimeOffset.MinValue;
        assessments.Clear();
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

    private sealed record CachedAssessment(
        DateTimeOffset ObservedAt,
        GilVendorAccessAssessment Assessment);
}

public sealed class DalamudOrdinaryGilShop : IDisposable
{
    private const string ShopAddon = "Shop";
    private const string SelectYesnoAddon = "SelectYesno";
    private const int MaximumPromptTextLength = 160;
    private static readonly IReadOnlySet<string> EmptyMenuEntries = new HashSet<string>();
    private readonly IGameGui gameGui;
    private readonly IReadOnlyDictionary<uint, IReadOnlySet<string>> menuEntriesByShopId;
    private readonly IAddonLifecycle? addonLifecycle;
    private bool disposed;

    // Per-submission confirmation state, reset immediately before each Shop callback.
    // Lifecycle callbacks and coordinator ticks both run on the framework thread.
    private long attemptSequence;
    private bool attemptActive;
    private bool fireCallbackReturned = true;
    private int promptOpenCount;
    private bool promptClosed;
    private bool promptConfirmedByUs;
    private int confirmedPromptGeneration;
    private string? promptText;

    public DalamudOrdinaryGilShop(IGameGui gameGui)
    {
        this.gameGui = gameGui ?? throw new ArgumentNullException(nameof(gameGui));
        menuEntriesByShopId = new Dictionary<uint, IReadOnlySet<string>>();
    }

    public DalamudOrdinaryGilShop(IGameGui gameGui, IDataManager dataManager)
        : this(gameGui, dataManager, addonLifecycle: null)
    {
    }

    /// <summary>
    /// With <paramref name="addonLifecycle"/>, SelectYesno setup/finalize events prove whether a
    /// purchase confirmation opened at all, even when another plugin (e.g. YesAlready) confirms
    /// it within the same frame. Dispose the shop to unregister those listeners.
    /// </summary>
    public DalamudOrdinaryGilShop(IGameGui gameGui, IDataManager dataManager, IAddonLifecycle? addonLifecycle)
    {
        this.gameGui = gameGui ?? throw new ArgumentNullException(nameof(gameGui));
        ArgumentNullException.ThrowIfNull(dataManager);
        menuEntriesByShopId = BuildMenuEntries(dataManager);
        this.addonLifecycle = addonLifecycle;
        if (addonLifecycle is not null)
        {
            addonLifecycle.RegisterListener(AddonEvent.PostSetup, SelectYesnoAddon, OnPromptSetup);
            addonLifecycle.RegisterListener(AddonEvent.PreFinalize, SelectYesnoAddon, OnPromptFinalize);
        }
    }

    public bool ObservesPromptLifecycle => addonLifecycle is not null && !disposed;

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
        var readiness = ReadPurchaseUiReadiness();
        if (readiness.State != Coordination.GilVendorPurchaseUiReadinessState.Ready)
        {
            error = $"ShopBlocked: the ordinary-gil shop cannot accept a purchase yet ({readiness.Describe()}); nothing was submitted.";
            return false;
        }

        AtkValue* values = stackalloc AtkValue[4];
        values[0].Type = ValueType.Int;
        values[0].Int = 0;
        values[1].Type = ValueType.UInt;
        values[1].UInt = checked((uint)row.RowIndex);
        values[2].Type = ValueType.UInt;
        values[2].UInt = quantity;
        values[3].Type = ValueType.Int;
        values[3].Int = 0;
        BeginAttempt();
        // A false return is recorded, not trusted as rejection on its own: other addons
        // (Trade) return false while still acting. When lifecycle events are attached, the
        // coordinator proves "no confirmation ever opened" before treating it as unsent;
        // without them, receipt verification decides and nothing is resent.
        fireCallbackReturned = addon->FireCallback(4, values, true);
        error = string.Empty;
        return true;
    }

    /// <summary>
    /// Live, zero-wait readiness for a purchase callback: the shop is set up and visible,
    /// owns no blocking child addon, and no SelectYesno is still on screen.
    /// </summary>
    public unsafe Coordination.GilVendorPurchaseUiReadiness ReadPurchaseUiReadiness()
    {
        var yesNoVisible = IsPresented(gameGui.GetAddonByName<AtkUnitBase>(SelectYesnoAddon, 1));
        var addon = gameGui.GetAddonByName<AtkUnitBase>(ShopAddon, 1);
        if (addon == null || !addon->IsVisible)
            return new(Coordination.GilVendorPurchaseUiReadinessState.ShopClosed, false, 0, yesNoVisible);
        var blocking = (int)addon->NumBlockingAddons;
        var ready = addon->IsReady && blocking == 0 && !yesNoVisible;
        return new(
            ready ? Coordination.GilVendorPurchaseUiReadinessState.Ready : Coordination.GilVendorPurchaseUiReadinessState.Blocked,
            addon->IsReady,
            blocking,
            yesNoVisible);
    }

    /// <summary>Confirmation-prompt evidence for the latest <see cref="TrySubmitPurchase"/> call.</summary>
    public unsafe Coordination.GilVendorPurchasePromptObservation ObservePurchasePrompt()
    {
        var readiness = ReadPurchaseUiReadiness();
        if (attemptActive && readiness.SelectYesnoVisible)
        {
            // Polling sighting covers hosts without lifecycle events; it never lowers evidence.
            if (promptOpenCount == 0)
                promptOpenCount = 1;
            var yesNo = gameGui.GetAddonByName<AddonSelectYesno>(SelectYesnoAddon, 1);
            if (ReadPromptText(yesNo) is { } visibleText)
                promptText = visibleText;
        }
        return new()
        {
            AttemptSequence = attemptSequence,
            LifecycleObserved = attemptActive && ObservesPromptLifecycle,
            FireCallbackReturned = fireCallbackReturned,
            PromptOpenCount = promptOpenCount,
            PromptClosed = promptClosed,
            PromptConfirmedByUs = promptConfirmedByUs,
            PromptText = promptText,
            ShopOpen = readiness.ShopOpen,
            ShopBlockingAddons = readiness.ShopBlockingAddons,
            SelectYesnoVisible = readiness.SelectYesnoVisible,
        };
    }

    /// <summary>
    /// Clicks Yes at most once per submission, and only on a ready SelectYesno whose text names
    /// <paramref name="expectedGil"/> (and <paramref name="quantity"/> when above one). A visible
    /// prompt that does not match is reported, never clicked.
    /// </summary>
    public unsafe Coordination.GilVendorPromptConfirmResult ConfirmPurchasePrompt(ulong expectedGil, uint quantity)
    {
        var addon = gameGui.GetAddonByName<AddonSelectYesno>(SelectYesnoAddon, 1);
        if (addon == null || !addon->AtkUnitBase.IsReady || !addon->AtkUnitBase.IsVisible)
            return new(Coordination.GilVendorPromptConfirmState.NotPresent, null);
        if (attemptActive && promptOpenCount == 0)
            promptOpenCount = 1;
        var text = ReadPromptText(addon);
        if (attemptActive && text is not null)
            promptText = text;
        if (promptConfirmedByUs)
        {
            // Only lifecycle events can tell a second prompt instance from the one still closing.
            return ObservesPromptLifecycle && promptOpenCount > confirmedPromptGeneration
                ? new(Coordination.GilVendorPromptConfirmState.UnexpectedPrompt, text)
                : new(Coordination.GilVendorPromptConfirmState.AlreadyConfirmed, text);
        }
        if (string.IsNullOrWhiteSpace(text))
            return new(Coordination.GilVendorPromptConfirmState.Waiting, null);
        if (!PromptMatchesPurchase(text, expectedGil, quantity))
            return new(Coordination.GilVendorPromptConfirmState.UnexpectedPrompt, text);
        if (addon->YesButton == null || !addon->YesButton->IsEnabled)
            return new(Coordination.GilVendorPromptConfirmState.Waiting, text);

        addon->YesButton->ClickAddonButton(&addon->AtkUnitBase);
        promptConfirmedByUs = true;
        confirmedPromptGeneration = promptOpenCount;
        return new(Coordination.GilVendorPromptConfirmState.Confirmed, text);
    }

    /// <summary>
    /// Unvalidated legacy confirmation kept for existing callers; still clicks at most once per
    /// submission. Prefer <see cref="ConfirmPurchasePrompt"/>.
    /// </summary>
    public unsafe bool TryConfirmOwnedPrompt()
    {
        if (promptConfirmedByUs)
            return false;
        var addon = gameGui.GetAddonByName<AddonSelectYesno>(SelectYesnoAddon, 1);
        if (addon == null ||
            !addon->AtkUnitBase.IsReady ||
            !addon->AtkUnitBase.IsVisible ||
            addon->YesButton == null ||
            !addon->YesButton->IsEnabled)
        {
            return false;
        }

        addon->YesButton->ClickAddonButton(&addon->AtkUnitBase);
        promptConfirmedByUs = true;
        confirmedPromptGeneration = Math.Max(1, promptOpenCount);
        return true;
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        if (addonLifecycle is null)
            return;
        addonLifecycle.UnregisterListener(AddonEvent.PreFinalize, SelectYesnoAddon, OnPromptFinalize);
        addonLifecycle.UnregisterListener(AddonEvent.PostSetup, SelectYesnoAddon, OnPromptSetup);
    }

    /// <summary>
    /// Matches the localized gil-shop confirmation (Addon 3406, e.g. "Purchase 99 Bronze Ingots
    /// for 1,089 gil?") by its numbers only, so pluralized or localized item names never matter.
    /// Thousands separators (",", ".", "'", and space variants) are ignored.
    /// </summary>
    internal static bool PromptMatchesPurchase(string text, ulong expectedGil, uint quantity)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var strict = ExtractNumbers(text, allowSpaceSeparators: false);
        var loose = ExtractNumbers(text, allowSpaceSeparators: true);
        bool Names(ulong value) => strict.Contains(value) || loose.Contains(value);
        return Names(expectedGil) && (quantity <= 1 || Names(quantity));
    }

    private static HashSet<ulong> ExtractNumbers(string text, bool allowSpaceSeparators)
    {
        var numbers = new HashSet<ulong>();
        ulong current = 0;
        var inNumber = false;
        var overflow = false;
        for (var index = 0; index < text.Length; index++)
        {
            var digit = DigitValue(text[index]);
            if (digit >= 0)
            {
                if (current > (ulong.MaxValue - (ulong)digit) / 10)
                    overflow = true;
                else
                    current = (current * 10) + (ulong)digit;
                inNumber = true;
                continue;
            }
            if (inNumber &&
                IsThousandsSeparator(text[index], allowSpaceSeparators) &&
                index + 1 < text.Length &&
                DigitValue(text[index + 1]) >= 0)
            {
                continue;
            }
            if (inNumber && !overflow)
                numbers.Add(current);
            current = 0;
            inNumber = false;
            overflow = false;
        }
        if (inNumber && !overflow)
            numbers.Add(current);
        return numbers;
    }

    private static int DigitValue(char value) => value switch
    {
        >= '0' and <= '9' => value - '0',
        >= '０' and <= '９' => value - '０',
        _ => -1,
    };

    private static bool IsThousandsSeparator(char value, bool allowSpaceSeparators) => value switch
    {
        ',' or '.' or '\'' or '’' => true,
        ' ' or ' ' or ' ' or ' ' => allowSpaceSeparators,
        _ => false,
    };

    private void BeginAttempt()
    {
        attemptSequence++;
        attemptActive = true;
        fireCallbackReturned = false;
        promptOpenCount = 0;
        promptClosed = false;
        promptConfirmedByUs = false;
        confirmedPromptGeneration = 0;
        promptText = null;
    }

    private unsafe void OnPromptSetup(AddonEvent eventType, AddonArgs args)
    {
        try
        {
            if (!attemptActive)
                return;
            promptOpenCount++;
            promptClosed = false;
            if (ReadPromptText((AddonSelectYesno*)args.Addon.Address) is { } text)
                promptText = text;
        }
        catch
        {
            // Opening is already recorded; text is diagnostic only.
        }
    }

    private void OnPromptFinalize(AddonEvent eventType, AddonArgs args)
    {
        if (attemptActive && promptOpenCount > 0)
            promptClosed = true;
    }

    private static unsafe string? ReadPromptText(AddonSelectYesno* addon)
    {
        if (addon == null)
            return null;
        string? text = null;
        if (addon->PromptText != null)
            text = addon->PromptText->NodeText.ExtractText();
        if (string.IsNullOrWhiteSpace(text) &&
            addon->AtkUnitBase.AtkValues != null &&
            addon->AtkUnitBase.AtkValuesCount > 0)
        {
            var value = addon->AtkUnitBase.AtkValues;
            if (value->Type is ValueType.String or ValueType.ManagedString or ValueType.WideString or ValueType.ConstString)
                text = value->GetValueAsString();
        }
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var singleLine = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return singleLine.Length <= MaximumPromptTextLength
            ? singleLine
            : singleLine[..MaximumPromptTextLength] + "…";
    }

    public unsafe void Close()
    {
        var addon = gameGui.GetAddonByName<AtkUnitBase>(ShopAddon, 1);
        if (addon != null && addon->IsVisible)
            addon->Close(true);
    }

    public unsafe bool TryCloseOfferMenu()
    {
        var closed = false;
        foreach (var addonName in new[] { "SelectString", "SelectIconString" })
        {
            var addon = gameGui.GetAddonByName<AtkUnitBase>(addonName, 1);
            if (!IsPresented(addon))
                continue;
            addon->Close(true);
            closed = true;
        }
        return closed;
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
