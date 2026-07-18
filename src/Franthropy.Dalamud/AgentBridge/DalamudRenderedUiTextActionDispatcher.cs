using System.Diagnostics;
using System.Runtime.InteropServices;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using ECommons.Automation.UIInput;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Franthropy.Dalamud.AgentBridge;

public sealed record RenderedUiTextMatch(string NodePath, string ParentPath, int Left, int Top, int Right, int Bottom);

public sealed record RenderedUiTextNode(
    string Text,
    string NodePath,
    string ParentPath,
    int Left,
    int Top,
    int Right,
    int Bottom);

public sealed record RenderedUiTextCaptureResult(
    bool Available,
    string Code,
    string Message,
    string AddonName,
    IReadOnlyList<RenderedUiTextNode> TextNodes);

public enum RenderedUiClickDispatchMode
{
    MouseClick,
    MouseDoubleClick,
    MouseDownUp,
    MouseDown,
}

public sealed record RenderedUiHitTarget(
    string NodePath,
    string ParentPath,
    int Left,
    int Top,
    int Right,
    int Bottom,
    RenderedUiClickDispatchMode DispatchMode);

public sealed record RenderedUiTextActionSelection(
    bool Success,
    string Code,
    string Message,
    string? TargetNodePath,
    RenderedUiClickDispatchMode? DispatchMode)
{
    public static RenderedUiTextActionSelection Fail(string code, string message) => new(false, code, message, null, null);
}

public sealed record RenderedUiTextActionResult(bool Success, string Code, string Message, string? AddonName, string? TargetNodePath);

public sealed record RenderedRetainerListActivationRequest(bool Success, int Command, uint RowIndex, string Code, string Message);

/// <summary>
/// Constrains retainer activation to the game's ten rendered retainer rows and the documented
/// RetainerList selection command. Keeping this policy pure makes the callback contract testable
/// without exposing a general-purpose addon callback escape hatch.
/// </summary>
public static class RenderedRetainerListActivationPolicy
{
    public static RenderedRetainerListActivationRequest Create(int rowIndex) => rowIndex is >= 0 and < 10
        ? new(true, 2, (uint)rowIndex, "RenderedRetainerActivationAuthorized", "The rendered retainer row is within the supported callback contract.")
        : new(false, 0, 0, "RenderedRetainerRowOutOfRange", "The rendered retainer row is outside the supported callback contract.");
}

/// <summary>
/// Pure selection policy for resolving one rendered text component to its registered hit target.
/// Ambiguous text components fail closed instead of choosing by traversal order.
/// </summary>
public static class RenderedUiTextActionSelector
{
    public static RenderedUiTextActionSelection Select(
        IReadOnlyList<RenderedUiTextMatch> matches,
        IReadOnlyList<RenderedUiHitTarget> hitTargets)
    {
        ArgumentNullException.ThrowIfNull(matches);
        ArgumentNullException.ThrowIfNull(hitTargets);
        var parents = matches.Select(value => value.ParentPath).Distinct(StringComparer.Ordinal).ToArray();
        if (parents.Length == 0)
            return RenderedUiTextActionSelection.Fail("RenderedTextNotFound", "The requested text is not currently rendered.");
        if (parents.Length != 1)
            return RenderedUiTextActionSelection.Fail("RenderedTextAmbiguous", "The requested text is rendered by more than one component.");

        var text = matches.First(value => string.Equals(value.ParentPath, parents[0], StringComparison.Ordinal));
        var centerX = (text.Left + text.Right) / 2;
        var centerY = (text.Top + text.Bottom) / 2;
        var target = hitTargets
            .Where(value => BelongsToTextComponent(value, text) &&
                            value.Left <= centerX && centerX <= value.Right &&
                            value.Top <= centerY && centerY <= value.Bottom)
            .OrderBy(value => Math.Max(0, value.Right - value.Left) * Math.Max(0, value.Bottom - value.Top))
            .ThenBy(value => value.NodePath, StringComparer.Ordinal)
            .FirstOrDefault();
        return target == null
            ? RenderedUiTextActionSelection.Fail("RenderedHitTargetNotFound", "The rendered text has no registered click target covering it.")
            : new(true, "RenderedHitTargetSelected", "A unique registered click target covers the rendered text.", target.NodePath, target.DispatchMode);
    }

    private static bool BelongsToTextComponent(RenderedUiHitTarget target, RenderedUiTextMatch text) =>
        string.Equals(target.ParentPath, text.ParentPath, StringComparison.Ordinal) ||
        string.Equals(target.NodePath, text.ParentPath, StringComparison.Ordinal) ||
        text.ParentPath.StartsWith($"{target.NodePath}/", StringComparison.Ordinal);

    public static RenderedUiTextActionSelection SelectNearestLeft(
        IReadOnlyList<RenderedUiTextMatch> matches,
        IReadOnlyList<RenderedUiHitTarget> hitTargets,
        int maximumGap = 16)
    {
        ArgumentNullException.ThrowIfNull(matches);
        ArgumentNullException.ThrowIfNull(hitTargets);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumGap);
        var parents = matches.Select(value => value.ParentPath).Distinct(StringComparer.Ordinal).ToArray();
        if (parents.Length == 0)
            return RenderedUiTextActionSelection.Fail("RenderedTextNotFound", "The requested text is not currently rendered.");
        if (parents.Length != 1)
            return RenderedUiTextActionSelection.Fail("RenderedTextAmbiguous", "The requested text is rendered by more than one component.");

        var text = matches.First(value => string.Equals(value.ParentPath, parents[0], StringComparison.Ordinal));
        var target = hitTargets
            .Where(value => SharesImmediateComponentScope(value, text) &&
                            value.Right <= text.Left && text.Left - value.Right <= maximumGap &&
                            value.Top < text.Bottom && text.Top < value.Bottom)
            .OrderBy(value => text.Left - value.Right)
            .ThenBy(value => Math.Max(0, value.Right - value.Left) * Math.Max(0, value.Bottom - value.Top))
            .ThenBy(value => value.NodePath, StringComparer.Ordinal)
            .FirstOrDefault();
        return target == null
            ? RenderedUiTextActionSelection.Fail("RenderedAdjacentTargetNotFound", "The rendered text has no unique registered control immediately to its left.")
            : new(true, "RenderedAdjacentTargetSelected", "A unique registered control is immediately left of the rendered text.", target.NodePath, target.DispatchMode);
    }

    private static bool SharesImmediateComponentScope(RenderedUiHitTarget target, RenderedUiTextMatch text)
    {
        if (string.Equals(target.ParentPath, text.ParentPath, StringComparison.Ordinal))
            return true;
        var separator = target.ParentPath.LastIndexOf('/');
        return separator > 0 && string.Equals(target.ParentPath[..separator], text.ParentPath, StringComparison.Ordinal);
    }
}

/// <summary>
/// Dispatches a registered click event to a unique, already-rendered text-bearing UI component.
/// This does not move the operating-system cursor, activate the game window, inspect the object
/// table, mutate the game target manager, or call native game-object interaction functions.
/// </summary>
public sealed class DalamudRenderedUiTextActionDispatcher
{
    private readonly IGameGui gameGui;

    public DalamudRenderedUiTextActionDispatcher(IGameGui gameGui) =>
        this.gameGui = gameGui ?? throw new ArgumentNullException(nameof(gameGui));

    /// <summary>
    /// Captures only text that is currently rendered by one named addon. This is deliberately
    /// usable before a local player exists so launcher, title, lobby, and queue workflows can use
    /// visible UI as their authority instead of lobby agents, packets, or cached character data.
    /// </summary>
    public unsafe RenderedUiTextCaptureResult CaptureVisibleText(string addonName)
    {
        if (string.IsNullOrWhiteSpace(addonName) || addonName.Length > 64)
            return new(false, "InvalidRenderedAddon", "A valid addon name is required.", addonName ?? string.Empty, []);

        var addon = gameGui.GetAddonByName<AtkUnitBase>(addonName, 1);
        if (addon == null)
            addon = FindVisibleLoadedAddon(addonName);
        if (addon == null || addon->RootNode == null || !addon->RootNode->IsVisible() || !addon->IsReady)
            return new(false, "RenderedAddonUnavailable", $"The rendered {addonName} addon is unavailable.", addonName, []);

        var nodes = new List<RenderedUiTextNode>();
        CaptureAllText(&addon->UldManager, addonName, nodes, new HashSet<nint>());
        var ordered = nodes
            .OrderBy(value => value.Top)
            .ThenBy(value => value.Left)
            .ThenBy(value => value.NodePath, StringComparer.Ordinal)
            .Take(512)
            .ToArray();
        return new(true, "RenderedAddonCaptured", $"Captured {ordered.Length} rendered text node(s) from {addonName}.", addonName, ordered);
    }

    public unsafe RenderedUiTextActionResult TryClickUniqueText(string addonName, string visibleText)
        => TryDispatchUniqueText(addonName, visibleText, rolloverOnly: false, activateFromRollover: false, selectNearestLeft: false, doubleClickOnly: false);

    public unsafe RenderedUiTextActionResult TryDoubleClickUniqueText(string addonName, string visibleText)
        => TryDispatchUniqueText(addonName, visibleText, rolloverOnly: false, activateFromRollover: false, selectNearestLeft: false, doubleClickOnly: true);

    public unsafe RenderedUiTextActionResult TrySelectUniqueListRowText(string addonName, string visibleText)
    {
        if (string.IsNullOrWhiteSpace(addonName) || string.IsNullOrWhiteSpace(visibleText))
            return Fail("InvalidRenderedTextAction", "Addon name and visible text are required.", addonName);
        var addon = gameGui.GetAddonByName<AtkUnitBase>(addonName, 1);
        if (addon == null)
            addon = FindVisibleLoadedAddon(addonName);
        if (addon == null || addon->RootNode == null || !addon->RootNode->IsVisible() || !addon->IsReady)
            return Fail("RenderedAddonUnavailable", $"The rendered {addonName} addon is unavailable.", addonName);

        var matches = new List<RenderedUiTextMatch>();
        var targets = new List<RenderedUiHitTarget>();
        CaptureManager(&addon->UldManager, addonName, visibleText.Trim(), false, false, matches, targets, new HashSet<nint>());
        var selection = RenderedUiTextActionSelector.Select(matches, targets);
        if (!selection.Success || selection.TargetNodePath == null)
            return new(false, selection.Code, selection.Message, addonName, null);

        var rowSeparator = selection.TargetNodePath.LastIndexOf('/');
        if (rowSeparator <= addonName.Length)
            return Fail("RenderedListRowNotFound", "The rendered text is not inside a list row.", addonName, selection.TargetNodePath);
        var rowPath = selection.TargetNodePath[..rowSeparator];
        var listSeparator = rowPath.LastIndexOf('/');
        if (listSeparator <= addonName.Length)
            return Fail("RenderedListNotFound", "The rendered row has no owning list.", addonName, selection.TargetNodePath);

        AtkComponentNode* rowNode = null;
        AtkComponentNode* listNode = null;
        var rowResNode = FindNodeByPath(addon, addonName, rowPath);
        if (rowResNode != null)
            rowNode = rowResNode->GetAsAtkComponentNode();
        var listResNode = FindNodeByPath(addon, addonName, rowPath[..listSeparator]);
        if (listResNode != null)
            listNode = listResNode->GetAsAtkComponentNode();
        if (rowNode == null || rowNode->Component == null ||
            rowNode->Component->GetComponentType() != ComponentType.ListItemRenderer ||
            listNode == null || listNode->Component == null || listNode->Component->GetComponentType() != ComponentType.List)
            return Fail("RenderedListStructureChanged", "The rendered text no longer resolves to a standard list row.", addonName, selection.TargetNodePath);

        var row = (AtkComponentListItemRenderer*)rowNode->Component;
        if (row->ListItemIndex < 0)
            return Fail("RenderedListRowUnavailable", "The rendered list row has no selectable index.", addonName, selection.TargetNodePath);
        var list = (AtkComponentList*)listNode->Component;
        list->SelectItem(row->ListItemIndex, true);
        if (list->SelectedItemIndex != row->ListItemIndex)
            return Fail("RenderedListSelectionRejected", "The rendered list rejected the requested row selection.", addonName, selection.TargetNodePath);
        return new(true, "RenderedListRowSelected", $"The unique rendered list row at index {row->ListItemIndex} was selected through its owning UI list.", addonName, selection.TargetNodePath);
    }

    /// <summary>
    /// Activates one retainer through the RetainerList addon's supported callback contract after
    /// proving that its name is rendered exactly once and resolving the owning list row. This is
    /// deliberately narrower than an arbitrary addon callback API: rendered UI supplies identity,
    /// while the callback only replaces the unsafe synthetic double-click normally used to open it.
    /// Callers must still prove the expected rendered retainer menu before treating activation as
    /// complete.
    /// </summary>
    public unsafe RenderedUiTextActionResult TryActivateUniqueRetainerListRowText(string visibleText)
    {
        const string addonName = "RetainerList";
        var selected = TrySelectUniqueListRowText(addonName, visibleText);
        if (!selected.Success || selected.TargetNodePath == null)
            return selected;

        var addon = gameGui.GetAddonByName<AtkUnitBase>(addonName, 1);
        if (addon == null)
            addon = FindVisibleLoadedAddon(addonName);
        if (addon == null || addon->RootNode == null || !addon->RootNode->IsVisible() || !addon->IsReady)
            return Fail("RenderedAddonUnavailable", "The rendered retainer list changed before activation.", addonName, selected.TargetNodePath);

        var rowSeparator = selected.TargetNodePath.LastIndexOf('/');
        if (rowSeparator <= addonName.Length)
            return Fail("RenderedListRowNotFound", "The rendered retainer name is no longer inside a list row.", addonName, selected.TargetNodePath);
        var rowNode = FindNodeByPath(addon, addonName, selected.TargetNodePath[..rowSeparator]);
        var componentNode = rowNode == null ? null : rowNode->GetAsAtkComponentNode();
        if (componentNode == null || componentNode->Component == null ||
            componentNode->Component->GetComponentType() != ComponentType.ListItemRenderer)
            return Fail("RenderedListStructureChanged", "The rendered retainer name no longer resolves to a standard list row.", addonName, selected.TargetNodePath);

        var row = (AtkComponentListItemRenderer*)componentNode->Component;
        var request = RenderedRetainerListActivationPolicy.Create(row->ListItemIndex);
        if (!request.Success)
            return Fail(request.Code, request.Message, addonName, selected.TargetNodePath);

        var values = stackalloc AtkValue[4];
        values[0] = new AtkValue { Type = AtkValueType.Int, Int = request.Command };
        values[1] = new AtkValue { Type = AtkValueType.UInt, UInt = request.RowIndex };
        values[2] = default;
        values[3] = default;
        if (!addon->FireCallback(4, values, true))
            return Fail("RenderedRetainerActivationRejected", "The retainer list rejected activation of the rendered row.", addonName, selected.TargetNodePath);

        return new(true, "RenderedRetainerActivationDispatched",
            $"Activated the unique rendered retainer row at index {row->ListItemIndex} through the RetainerList callback contract.",
            addonName, selected.TargetNodePath);
    }

    public unsafe RenderedUiTextActionResult TryRollOverUniqueText(string addonName, string visibleText)
        => TryDispatchUniqueText(addonName, visibleText, rolloverOnly: true, activateFromRollover: false, selectNearestLeft: false, doubleClickOnly: false);

    public unsafe RenderedUiTextActionResult TryActivateUniqueText(string addonName, string visibleText)
        => TryDispatchUniqueText(addonName, visibleText, rolloverOnly: true, activateFromRollover: true, selectNearestLeft: false, doubleClickOnly: false);

    /// <summary>
    /// Proves a unique rendered target label, rolls over its registered UI target, and submits the
    /// standard keyboard Confirm gesture to the game window. This does not move the physical cursor
    /// or activate the window, and it never discovers or interacts with a game object directly.
    /// </summary>
    public unsafe RenderedUiTextActionResult TryConfirmUniqueText(string addonName, string visibleText)
    {
        var selected = TryDispatchUniqueText(addonName, visibleText, rolloverOnly: true,
            activateFromRollover: false, selectNearestLeft: false, doubleClickOnly: false);
        if (!selected.Success)
            return selected;
        return PostConfirmKey()
            ? new(true, "RenderedTextConfirmDispatched",
                "The standard Confirm key was posted after proving the unique rendered target; the physical keyboard and window focus were unchanged.",
                addonName, selected.TargetNodePath)
            : Fail("RenderedTextConfirmRejected",
                "The game window rejected the standard Confirm key after the rendered target was proven.",
                addonName, selected.TargetNodePath);
    }

    public unsafe RenderedUiTextActionResult TryClickUniqueControlImmediatelyLeftOfText(string addonName, string visibleText)
        => TryDispatchUniqueText(addonName, visibleText, rolloverOnly: false, activateFromRollover: false, selectNearestLeft: true, doubleClickOnly: false);

    public unsafe RenderedUiTextActionResult TryActivateUniqueControlImmediatelyLeftOfText(string addonName, string visibleText)
        => TryDispatchUniqueText(addonName, visibleText, rolloverOnly: true, activateFromRollover: true, selectNearestLeft: true, doubleClickOnly: false);

    private unsafe RenderedUiTextActionResult TryDispatchUniqueText(
        string addonName,
        string visibleText,
        bool rolloverOnly,
        bool activateFromRollover,
        bool selectNearestLeft,
        bool doubleClickOnly)
    {
        if (string.IsNullOrWhiteSpace(addonName) || addonName.Length > 64 ||
            string.IsNullOrWhiteSpace(visibleText) || visibleText.Length > 256)
            return Fail("InvalidRenderedTextAction", "Addon name and visible text are required.", addonName);

        var addon = gameGui.GetAddonByName<AtkUnitBase>(addonName, 1);
        if (addon == null)
            addon = FindVisibleLoadedAddon(addonName);
        if (addon == null || addon->RootNode == null || !addon->RootNode->IsVisible() || !addon->IsReady)
            return Fail("RenderedAddonUnavailable", $"The rendered {addonName} addon is unavailable.", addonName);

        var matches = new List<RenderedUiTextMatch>();
        var targets = new List<RenderedUiHitTarget>();
        CaptureManager(&addon->UldManager, addonName, visibleText.Trim(), rolloverOnly, doubleClickOnly, matches, targets, new HashSet<nint>());
        var selection = selectNearestLeft
            ? RenderedUiTextActionSelector.SelectNearestLeft(matches, targets)
            : RenderedUiTextActionSelector.Select(matches, targets);
        if (!selection.Success || selection.TargetNodePath == null || selection.DispatchMode == null)
            return new(false, selection.Code, selection.Message, addonName, null);

        var node = FindNodeByPath(addon, addonName, selection.TargetNodePath);
        var componentSeparator = selection.TargetNodePath.LastIndexOf('/');
        AtkComponentNode* componentNode = null;
        AtkComponentNode* parentComponentNode = null;
        if (componentSeparator > addonName.Length)
        {
            var componentPath = selection.TargetNodePath[..componentSeparator];
            var componentResNode = FindNodeByPath(addon, addonName, componentPath);
            if (componentResNode != null)
                componentNode = componentResNode->GetAsAtkComponentNode();
            var parentSeparator = componentPath.LastIndexOf('/');
            if (parentSeparator > addonName.Length)
            {
                var parentResNode = FindNodeByPath(addon, addonName, componentPath[..parentSeparator]);
                if (parentResNode != null)
                    parentComponentNode = parentResNode->GetAsAtkComponentNode();
            }
        }
        if (node == null || !IsEffectivelyVisible(node) ||
            (rolloverOnly
                ? !node->IsEventRegistered(AtkEventType.MouseOver)
                : !Supports(node, selection.DispatchMode.Value)))
            return Fail("RenderedHitTargetStale", "The selected registered click target changed before dispatch.", addonName);

        FFXIVClientStructs.FFXIV.Common.Math.Bounds bounds;
        node->GetBounds(&bounds);
        var dispatched = rolloverOnly
            ? Dispatch(node, AtkEventType.MouseOver, bounds)
            : selection.DispatchMode.Value switch
        {
            RenderedUiClickDispatchMode.MouseClick => Dispatch(addon, componentNode, parentComponentNode, node, AtkEventType.MouseClick),
            RenderedUiClickDispatchMode.MouseDoubleClick => Dispatch(addon, componentNode, parentComponentNode, node, AtkEventType.MouseDoubleClick),
            RenderedUiClickDispatchMode.MouseDownUp =>
                Dispatch(addon, componentNode, parentComponentNode, node, AtkEventType.MouseDown) && Dispatch(addon, componentNode, parentComponentNode, node, AtkEventType.MouseUp),
            RenderedUiClickDispatchMode.MouseDown => Dispatch(addon, componentNode, parentComponentNode, node, AtkEventType.MouseDown),
            _ => false,
        };
        if (dispatched && activateFromRollover)
            dispatched = PostPointerActivation(bounds);
        return dispatched
            ? new(true,
                activateFromRollover
                    ? "RenderedTextActivationDispatched"
                    : rolloverOnly ? "RenderedTextRollOverDispatched" : "RenderedTextClickDispatched",
                activateFromRollover
                    ? "A client-area pointer activation was posted at the rendered text component without moving the physical cursor or activating the game window."
                    : rolloverOnly
                    ? "The registered rollover event was dispatched to the rendered text component."
                    : "The registered click event was dispatched to the rendered text component.",
                addonName,
                selection.TargetNodePath)
            : Fail(
                activateFromRollover
                    ? "RenderedTextActivationRejected"
                    : rolloverOnly ? "RenderedTextRollOverRejected" : "RenderedTextClickRejected",
                activateFromRollover
                    ? "The rendered text component rejected its standard activation sequence."
                    : rolloverOnly
                    ? "The rendered text component rejected its registered rollover event."
                    : "The rendered text component rejected its registered click event.",
                addonName,
                selection.TargetNodePath);
    }

    private static unsafe bool Dispatch(
        AtkResNode* node,
        AtkEventType eventType,
        FFXIVClientStructs.FFXIV.Common.Math.Bounds bounds)
    {
        var registered = (AtkEvent*)node->AtkEventManager.Event;
        while (registered != null && registered->State.EventType != eventType)
            registered = registered->NextEvent;
        if (registered == null)
            return false;
        var data = new AtkEventData
        {
            MouseData = new AtkEventData.AtkMouseData
            {
                PosX = (short)Math.Clamp((bounds.Pos1.X + bounds.Pos2.X) / 2, short.MinValue, short.MaxValue),
                PosY = (short)Math.Clamp((bounds.Pos1.Y + bounds.Pos2.Y) / 2, short.MinValue, short.MaxValue),
            },
        };
        if (registered->Listener == null)
            return false;
        registered->Listener->ReceiveEvent(eventType, (int)registered->Param, registered, &data);
        return true;
    }

    private static unsafe bool Dispatch(AtkUnitBase* addon, AtkComponentNode* componentNode, AtkComponentNode* parentComponentNode, AtkResNode* node, AtkEventType eventType)
    {
        var registered = (AtkEvent*)node->AtkEventManager.Event;
        while (registered != null && registered->State.EventType != eventType)
            registered = registered->NextEvent;
        if (registered == null)
            return false;
        if (componentNode != null && componentNode->Component != null &&
            componentNode->Component->GetComponentType() == ComponentType.Button)
        {
            (*(AtkComponentButton*)componentNode->Component).ClickAddonButton(addon);
            return true;
        }
        if (componentNode != null && componentNode->Component != null &&
            componentNode->Component->GetComponentType() == ComponentType.ListItemRenderer)
        {
            // ECommons' synthetic LIST_ITEM_DOUBLE_CLICK does not carry the opaque list event
            // payload expected by every native AtkComponentList implementation. Sending it can
            // dereference invalid event data and terminate the game. Select the row through its
            // owning list and submit ordinary Confirm input instead.
            if (eventType == AtkEventType.MouseDoubleClick)
                return false;
            var listEventType = eventType == AtkEventType.MouseDoubleClick ? AtkEventType.ListItemDoubleClick : AtkEventType.ListItemClick;
            var listEvent = (AtkEvent*)componentNode->AtkResNode.AtkEventManager.Event;
            while (listEvent != null && listEvent->State.EventType != listEventType)
                listEvent = listEvent->NextEvent;
            ClickHelper.ClickAddonComponent(
                parentComponentNode != null && parentComponentNode->Component != null &&
                parentComponentNode->Component->GetComponentType() == ComponentType.List
                    ? parentComponentNode->Component
                    : componentNode->Component,
                componentNode,
                listEvent != null ? listEvent->Param : registered->Param,
                eventType == AtkEventType.MouseDoubleClick
                    ? ECommons.Automation.UIInput.EventType.LIST_ITEM_DOUBLE_CLICK
                    : ECommons.Automation.UIInput.EventType.LIST_ITEM_CLICK);
            return true;
        }
        if (componentNode != null && componentNode->Component != null)
        {
            ClickHelper.ClickAddonComponent(
                componentNode->Component,
                componentNode,
                registered->Param,
                ECommons.Automation.UIInput.EventType.CHANGE);
            return true;
        }
        addon->ReceiveEvent(eventType, (int)registered->Param, registered, null);
        return true;
    }

    private static bool PostPointerActivation(FFXIVClientStructs.FFXIV.Common.Math.Bounds bounds)
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var window = process.MainWindowHandle;
        if (window == nint.Zero)
            return false;

        var point = new NativeMethods.Point
        {
            X = (int)MathF.Round((bounds.Pos1.X + bounds.Pos2.X) / 2),
            Y = (int)MathF.Round((bounds.Pos1.Y + bounds.Pos2.Y) / 2),
        };
        if (!NativeMethods.ScreenToClient(window, ref point))
            return false;
        var x = Math.Clamp(point.X, 0, ushort.MaxValue);
        var y = Math.Clamp(point.Y, 0, ushort.MaxValue);
        var position = (nint)((y << 16) | (x & 0xffff));
        return NativeMethods.PostMessage(window, NativeMethods.WmMouseMove, nint.Zero, position) &&
               NativeMethods.PostMessage(window, NativeMethods.WmLeftButtonDown, (nint)NativeMethods.MkLeftButton, position) &&
               NativeMethods.PostMessage(window, NativeMethods.WmLeftButtonUp, nint.Zero, position);
    }

    private static bool PostConfirmKey()
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var window = process.MainWindowHandle;
        return window != nint.Zero &&
               NativeMethods.PostMessage(window, NativeMethods.WmKeyDown, (nint)NativeMethods.VkNumpad0, (nint)0x00520001) &&
               NativeMethods.PostMessage(window, NativeMethods.WmKeyUp, (nint)NativeMethods.VkNumpad0, unchecked((nint)(int)0xC0520001));
    }

    private static unsafe AtkUnitBase* FindVisibleLoadedAddon(string addonName)
    {
        var stage = AtkStage.Instance();
        var unitManager = stage == null ? null : (AtkUnitManager*)stage->RaptureAtkUnitManager;
        if (unitManager == null)
            return null;
        var loaded = &unitManager->AllLoadedUnitsList;
        for (var index = 0; index < loaded->Count; index++)
        {
            AtkUnitBase* candidate = loaded->Entries[index];
            if (candidate != null && candidate->RootNode != null && candidate->RootNode->IsVisible() &&
                string.Equals(candidate->NameString, addonName, StringComparison.Ordinal))
                return candidate;
        }
        return null;
    }

    private static unsafe void CaptureManager(
        AtkUldManager* manager,
        string path,
        string visibleText,
        bool rolloverOnly,
        bool doubleClickOnly,
        ICollection<RenderedUiTextMatch> matches,
        ICollection<RenderedUiHitTarget> targets,
        ISet<nint> visited)
    {
        if (manager == null || manager->NodeList == null || !visited.Add((nint)manager))
            return;
        for (var index = 0u; index < manager->NodeListCount; index++)
        {
            var node = manager->NodeList[index];
            if (node == null || !IsEffectivelyVisible(node))
                continue;
            var nodePath = $"{path}/{node->NodeId}";
            var parentPath = path;
            FFXIVClientStructs.FFXIV.Common.Math.Bounds bounds;
            node->GetBounds(&bounds);
            var dispatchMode = doubleClickOnly && node->IsEventRegistered(AtkEventType.MouseDoubleClick)
                ? RenderedUiClickDispatchMode.MouseDoubleClick
                : rolloverOnly && node->IsEventRegistered(AtkEventType.MouseOver)
                ? RenderedUiClickDispatchMode.MouseDown
                : doubleClickOnly ? null : ResolveDispatchMode(node);
            if (dispatchMode != null)
                targets.Add(new(nodePath, parentPath, bounds.Pos1.X, bounds.Pos1.Y, bounds.Pos2.X, bounds.Pos2.Y, dispatchMode.Value));

            var textNode = node->GetAsAtkTextNode();
            if (textNode != null && string.Equals(textNode->NodeText.ExtractText().Trim(), visibleText, StringComparison.OrdinalIgnoreCase))
                matches.Add(new(nodePath, parentPath, bounds.Pos1.X, bounds.Pos1.Y, bounds.Pos2.X, bounds.Pos2.Y));

            var componentNode = node->GetAsAtkComponentNode();
            if (componentNode != null && componentNode->Component != null)
                CaptureManager(&componentNode->Component->UldManager, nodePath, visibleText, rolloverOnly, doubleClickOnly, matches, targets, visited);
        }
    }

    private static unsafe void CaptureAllText(
        AtkUldManager* manager,
        string path,
        ICollection<RenderedUiTextNode> nodes,
        ISet<nint> visited)
    {
        if (manager == null || manager->NodeList == null || nodes.Count >= 512 || !visited.Add((nint)manager))
            return;
        for (var index = 0u; index < manager->NodeListCount && nodes.Count < 512; index++)
        {
            var node = manager->NodeList[index];
            if (node == null || !IsEffectivelyVisible(node))
                continue;
            var nodePath = $"{path}/{node->NodeId}";
            FFXIVClientStructs.FFXIV.Common.Math.Bounds bounds;
            node->GetBounds(&bounds);
            var textNode = node->GetAsAtkTextNode();
            var text = textNode == null ? string.Empty : textNode->NodeText.ExtractText().Trim();
            if (!string.IsNullOrWhiteSpace(text))
            {
                nodes.Add(new(
                    text.Length <= 512 ? text : text[..512],
                    nodePath,
                    path,
                    bounds.Pos1.X,
                    bounds.Pos1.Y,
                    bounds.Pos2.X,
                    bounds.Pos2.Y));
            }

            var componentNode = node->GetAsAtkComponentNode();
            if (componentNode != null && componentNode->Component != null)
                CaptureAllText(&componentNode->Component->UldManager, nodePath, nodes, visited);
        }
    }

    private static unsafe RenderedUiClickDispatchMode? ResolveDispatchMode(AtkResNode* node)
    {
        if (node->IsEventRegistered(AtkEventType.MouseClick))
            return RenderedUiClickDispatchMode.MouseClick;
        if (node->IsEventRegistered(AtkEventType.MouseDown) && node->IsEventRegistered(AtkEventType.MouseUp))
            return RenderedUiClickDispatchMode.MouseDownUp;
        if (node->IsEventRegistered(AtkEventType.MouseDown))
            return RenderedUiClickDispatchMode.MouseDown;
        return null;
    }

    private static unsafe bool Supports(AtkResNode* node, RenderedUiClickDispatchMode mode) => mode switch
    {
        RenderedUiClickDispatchMode.MouseClick => node->IsEventRegistered(AtkEventType.MouseClick),
        RenderedUiClickDispatchMode.MouseDoubleClick => node->IsEventRegistered(AtkEventType.MouseDoubleClick),
        RenderedUiClickDispatchMode.MouseDownUp =>
            node->IsEventRegistered(AtkEventType.MouseDown) && node->IsEventRegistered(AtkEventType.MouseUp),
        RenderedUiClickDispatchMode.MouseDown => node->IsEventRegistered(AtkEventType.MouseDown),
        _ => false,
    };

    private static unsafe AtkResNode* FindNodeByPath(AtkUnitBase* addon, string addonName, string nodePath)
    {
        var segments = nodePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2 || !string.Equals(segments[0], addonName, StringComparison.Ordinal))
            return null;
        var manager = &addon->UldManager;
        AtkResNode* node = null;
        for (var segmentIndex = 1; segmentIndex < segments.Length; segmentIndex++)
        {
            if (!uint.TryParse(segments[segmentIndex], out var nodeId) || manager == null || manager->NodeList == null)
                return null;
            node = null;
            for (var index = 0u; index < manager->NodeListCount; index++)
            {
                var candidate = manager->NodeList[index];
                if (candidate != null && candidate->NodeId == nodeId)
                {
                    node = candidate;
                    break;
                }
            }
            if (node == null)
                return null;
            if (segmentIndex + 1 < segments.Length)
            {
                var componentNode = node->GetAsAtkComponentNode();
                if (componentNode == null || componentNode->Component == null)
                    return null;
                manager = &componentNode->Component->UldManager;
            }
        }
        return node;
    }

    private static unsafe bool IsEffectivelyVisible(AtkResNode* node)
    {
        for (var current = node; current != null; current = current->ParentNode)
        {
            if (!current->IsVisible())
                return false;
        }
        return true;
    }

    private static RenderedUiTextActionResult Fail(string code, string message, string? addonName, string? nodePath = null) =>
        new(false, code, message, addonName, nodePath);

    private static class NativeMethods
    {
        internal const uint WmMouseMove = 0x0200;
        internal const uint WmLeftButtonDown = 0x0201;
        internal const uint WmLeftButtonUp = 0x0202;
        internal const uint WmKeyDown = 0x0100;
        internal const uint WmKeyUp = 0x0101;
        internal const uint MkLeftButton = 0x0001;
        internal const uint VkNumpad0 = 0x60;

        [StructLayout(LayoutKind.Sequential)]
        internal struct Point
        {
            public int X;
            public int Y;
        }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ScreenToClient(nint window, ref Point point);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
    }
}
