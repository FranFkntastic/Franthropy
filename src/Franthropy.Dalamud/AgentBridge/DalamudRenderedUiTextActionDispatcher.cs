using System.Diagnostics;
using System.Runtime.InteropServices;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using ECommons.Automation.UIInput;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Franthropy.Dalamud.AgentBridge;

public sealed record RenderedUiTextMatch(string NodePath, string ParentPath, int Left, int Top, int Right, int Bottom);

public enum RenderedUiClickDispatchMode
{
    MouseClick,
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

    public unsafe RenderedUiTextActionResult TryClickUniqueText(string addonName, string visibleText)
        => TryDispatchUniqueText(addonName, visibleText, rolloverOnly: false, activateFromRollover: false, selectNearestLeft: false);

    public unsafe RenderedUiTextActionResult TryRollOverUniqueText(string addonName, string visibleText)
        => TryDispatchUniqueText(addonName, visibleText, rolloverOnly: true, activateFromRollover: false, selectNearestLeft: false);

    public unsafe RenderedUiTextActionResult TryActivateUniqueText(string addonName, string visibleText)
        => TryDispatchUniqueText(addonName, visibleText, rolloverOnly: true, activateFromRollover: true, selectNearestLeft: false);

    public unsafe RenderedUiTextActionResult TryClickUniqueControlImmediatelyLeftOfText(string addonName, string visibleText)
        => TryDispatchUniqueText(addonName, visibleText, rolloverOnly: false, activateFromRollover: false, selectNearestLeft: true);

    public unsafe RenderedUiTextActionResult TryActivateUniqueControlImmediatelyLeftOfText(string addonName, string visibleText)
        => TryDispatchUniqueText(addonName, visibleText, rolloverOnly: true, activateFromRollover: true, selectNearestLeft: true);

    private unsafe RenderedUiTextActionResult TryDispatchUniqueText(
        string addonName,
        string visibleText,
        bool rolloverOnly,
        bool activateFromRollover,
        bool selectNearestLeft)
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
        CaptureManager(&addon->UldManager, addonName, visibleText.Trim(), rolloverOnly, matches, targets, new HashSet<nint>());
        var selection = selectNearestLeft
            ? RenderedUiTextActionSelector.SelectNearestLeft(matches, targets)
            : RenderedUiTextActionSelector.Select(matches, targets);
        if (!selection.Success || selection.TargetNodePath == null || selection.DispatchMode == null)
            return new(false, selection.Code, selection.Message, addonName, null);

        var node = FindNodeByPath(addon, addonName, selection.TargetNodePath);
        var componentSeparator = selection.TargetNodePath.LastIndexOf('/');
        AtkComponentNode* componentNode = null;
        if (componentSeparator > addonName.Length)
        {
            var componentResNode = FindNodeByPath(addon, addonName, selection.TargetNodePath[..componentSeparator]);
            if (componentResNode != null)
                componentNode = componentResNode->GetAsAtkComponentNode();
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
            RenderedUiClickDispatchMode.MouseClick => Dispatch(addon, componentNode, node, AtkEventType.MouseClick),
            RenderedUiClickDispatchMode.MouseDownUp =>
                Dispatch(addon, componentNode, node, AtkEventType.MouseDown) && Dispatch(addon, componentNode, node, AtkEventType.MouseUp),
            RenderedUiClickDispatchMode.MouseDown => Dispatch(addon, componentNode, node, AtkEventType.MouseDown),
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

    private static unsafe bool Dispatch(AtkUnitBase* addon, AtkComponentNode* componentNode, AtkResNode* node, AtkEventType eventType)
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
            var listEvent = (AtkEvent*)componentNode->AtkResNode.AtkEventManager.Event;
            while (listEvent != null && listEvent->State.EventType != AtkEventType.ListItemClick)
                listEvent = listEvent->NextEvent;
            if (listEvent == null)
                return false;
            ClickHelper.ClickAddonComponent(
                componentNode->Component,
                componentNode,
                listEvent->Param,
                ECommons.Automation.UIInput.EventType.LIST_ITEM_CLICK);
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
            var dispatchMode = rolloverOnly && node->IsEventRegistered(AtkEventType.MouseOver)
                ? RenderedUiClickDispatchMode.MouseDown
                : ResolveDispatchMode(node);
            if (dispatchMode != null)
                targets.Add(new(nodePath, parentPath, bounds.Pos1.X, bounds.Pos1.Y, bounds.Pos2.X, bounds.Pos2.Y, dispatchMode.Value));

            var textNode = node->GetAsAtkTextNode();
            if (textNode != null && string.Equals(textNode->NodeText.ExtractText().Trim(), visibleText, StringComparison.OrdinalIgnoreCase))
                matches.Add(new(nodePath, parentPath, bounds.Pos1.X, bounds.Pos1.Y, bounds.Pos2.X, bounds.Pos2.Y));

            var componentNode = node->GetAsAtkComponentNode();
            if (componentNode != null && componentNode->Component != null)
                CaptureManager(&componentNode->Component->UldManager, nodePath, visibleText, rolloverOnly, matches, targets, visited);
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
        internal const uint MkLeftButton = 0x0001;

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
