using Dalamud.Plugin.Services;
using ECommons;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Franthropy.Dalamud.AgentBridge;
using Lumina.Excel.Sheets;

namespace Franthropy.Dalamud.Travel;

public sealed record DataCenterServiceAccountChoice(string PromptText, string EntryText);
public sealed record DataCenterConnectionObservation(string Phase, string Code, string? CanonicalDataCenter = null,
    string? NextInput = null, uint? CanonicalDataCenterRowId = null);

/// <summary>No native input was attempted; the owner may safely clear its intent and observe again.</summary>
public sealed class DataCenterPreInputRefusal(string message) : InvalidOperationException(message);

/// <summary>Framework-thread-only, character-free lobby steps. The owner journals each input before dispatch.</summary>
public sealed unsafe class DalamudDataCenterConnection(IDataManager data, IClientState client, IObjectTable objects, IGameGui gameGui)
{
    private readonly DalamudRenderedUiTextActionDispatcher renderedUi = new(gameGui);

    public string[] AvailableDataCenters() => data.GetExcelSheet<WorldDCGroupType>()
        .Where(dc => dc.Region.RowId != 0 && AddonMaster.TitleDCWorldMap.PublicDC.Contains((int)dc.RowId))
        .Select(dc => dc.Name.ToString()).Where(name => !string.IsNullOrWhiteSpace(name)).ToArray();

    private static bool Ready(string name, out AtkUnitBase* addon) =>
        GenericHelpers.TryGetAddonByName(name, out addon) && addon->IsVisible && GenericHelpers.IsAddonReady(addon);

    public DataCenterConnectionObservation Observe(string requested, IReadOnlySet<string> sent,
        DataCenterServiceAccountChoice? account)
    {
        if (client.IsLoggedIn || objects.LocalPlayer is not null) return new("Blocked", "ClientLoggedIn");
        var matches = data.GetExcelSheet<WorldDCGroupType>()
            .Where(dc => dc.Region.RowId != 0 && dc.Name.ToString().Equals(requested, StringComparison.OrdinalIgnoreCase)
                && AddonMaster.TitleDCWorldMap.PublicDC.Contains((int)dc.RowId)).ToArray();
        if (matches.Length != 1) return new("Blocked", "DataCenterUnavailable");
        var dc = matches[0];
        var canonical = dc.Name.ToString();
        if (Ready("TitleConnect", out _)) return new("Connecting", "TitleConnectionInProgress", canonical);
        if (Ready("SelectYesno", out _) || Ready("_Error", out _)) return new("Blocked", "UnexpectedDialog", canonical);
        if (Ready("SelectString", out var select))
        {
            if (!sent.Contains("SelectDataCenter") || account is null) return new("Blocked", "ServiceAccountChoiceRequired", canonical);
            var menu = new AddonMaster.SelectString(select);
            if (menu.Text != account.PromptText || menu.Entries.Count(entry => entry.Text == account.EntryText) != 1)
                return new("Blocked", "ServiceAccountPromptMismatch", canonical);
            return new("Connecting", "ServiceAccount", canonical, sent.Contains("ServiceAccount") ? null : "ServiceAccount");
        }
        if (Ready("TitleDCWorldMap", out _))
            return new("SelectingDataCenter", "SelectorReady", canonical, sent.Contains("SelectDataCenter") ? null : "SelectDataCenter", dc.RowId);
        if (Ready("_CharaSelectListMenu", out _))
        {
            const string addonName = "_CharaSelectListMenu";
            var capture = renderedUi.CaptureVisibleText(addonName);
            if (!capture.Available) return new("Blocked", "RenderedLobbyUnavailable", canonical);
            // Only addon-direct text is current-lobby evidence. Nested roster names must never
            // masquerade as the DC header or current world, even when they match sheet names.
            var rootText = capture.TextNodes.Where(node => node.ParentPath == addonName).Select(node => node.Text).ToArray();
            var headers = rootText.Where(text => text.StartsWith('[') && text.EndsWith(']')).ToArray();
            if (headers.Length != 1) return new("Blocked", "RenderedLobbyHeaderAmbiguous", canonical);
            var headerName = headers[0][1..^1].Trim();
            var headerDataCenters = data.GetExcelSheet<WorldDCGroupType>()
                .Where(value => value.Region.RowId != 0 && AddonMaster.TitleDCWorldMap.PublicDC.Contains((int)value.RowId)
                    && value.Name.ToString() == headerName).ToArray();
            var worlds = data.GetExcelSheet<World>().Where(world => world.IsPublic).ToArray();
            var currentWorldNames = rootText.Where(text => worlds.Any(world => world.Name.ToString() == text)).ToArray();
            if (headerDataCenters.Length != 1 || headerDataCenters[0].RowId != dc.RowId || currentWorldNames.Length != 1)
                return new("Blocked", "RenderedLobbyMismatch", canonical);
            var currentWorlds = worlds.Where(world => world.Name.ToString() == currentWorldNames[0]).ToArray();
            // The rendered header owns the current lobby connection. Static World.DataCenter
            // membership is not connection evidence; only validate the visible public world identity.
            if (currentWorlds.Length != 1)
                return new("Blocked", "RenderedLobbyMismatch", canonical);
            return new("CharacterSelectionReady", sent.Contains("SelectDataCenter") ? "RenderedLobbyMatches" : "AlreadyReady", canonical);
        }
        if (Ready("_TitleMenu", out var title) && new AddonMaster._TitleMenu(title).IsReady && !sent.Contains("OpenSelector"))
            return new("WaitingForTitle", "TitleReady", canonical, "OpenSelector");
        return new(sent.Contains("SelectDataCenter") ? "Connecting" : "WaitingForTitle", "WaitingForUi", canonical);
    }

    public void Dispatch(string requested, IReadOnlySet<string> sent, DataCenterServiceAccountChoice? account, string input)
    {
        DataCenterConnectionObservation observed;
        try { observed = Observe(requested, sent, account); }
        catch (Exception ex) { throw new DataCenterPreInputRefusal($"Lobby observation unavailable: {ex.Message}"); }
        if (observed.NextInput != input) throw new DataCenterPreInputRefusal("Lobby state changed before dispatch.");
        switch (input)
        {
            case "OpenSelector":
                if (!Ready("_TitleMenu", out var title)) throw new DataCenterPreInputRefusal("Title unavailable.");
                new AddonMaster._TitleMenu(title).DataCenter();
                break;
            case "SelectDataCenter":
                if (observed.CanonicalDataCenterRowId is not uint rowId ||
                    !AddonMaster.TitleDCWorldMap.PublicDC.Contains((int)rowId) || !Ready("TitleDCWorldMap", out var map))
                    throw new DataCenterPreInputRefusal("Validated public data center or selector unavailable.");
                if (!new AddonMaster.TitleDCWorldMap(map).Select((int)rowId))
                    throw new InvalidOperationException("Data center selection refused.");
                break;
            case "ServiceAccount":
                if (!Ready("SelectString", out var menu)) throw new DataCenterPreInputRefusal("Account prompt unavailable.");
                var entries = new AddonMaster.SelectString(menu).Entries.Where(entry => entry.Text == account!.EntryText).ToArray();
                if (entries.Length != 1) throw new DataCenterPreInputRefusal("Account entry changed before input.");
                entries[0].Select();
                break;
            default: throw new DataCenterPreInputRefusal("Unsupported typed lobby input.");
        }
    }
}
