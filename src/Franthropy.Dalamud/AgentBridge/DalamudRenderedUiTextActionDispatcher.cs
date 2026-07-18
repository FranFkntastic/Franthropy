using Dalamud.Plugin.Services;
using Dalamud.Utility;
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
            .Where(value => string.Equals(value.ParentPath, parents[0], StringComparison.Ordinal) &&
                            value.Left <= centerX && centerX <= value.Right &&
                            value.Top <= centerY && centerY <= value.Bottom)
            .OrderBy(value => Math.Max(0, value.Right - value.Left) * Math.Max(0, value.Bottom - value.Top))
            .ThenBy(value => value.NodePath, StringComparer.Ordinal)
            .FirstOrDefault();
        return target == null
            ? RenderedUiTextActionSelection.Fail("RenderedHitTargetNotFound", "The rendered text has no registered click target covering it.")
            : new(true, "RenderedHitTargetSelected", "A unique registered click target covers the rendered text.", target.NodePath, target.DispatchMode);
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
        => TryDispatchUniqueText(addonName, visibleText, rolloverOnly: false);

    public unsafe RenderedUiTextActionResult TryRollOverUniqueText(string addonName, string visibleText)
        => TryDispatchUniqueText(addonName, visibleText, rolloverOnly: true);

    private unsafe RenderedUiTextActionResult TryDispatchUniqueText(string addonName, string visibleText, bool rolloverOnly)
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
        var selection = RenderedUiTextActionSelector.Select(matches, targets);
        if (!selection.Success || selection.TargetNodePath == null || selection.DispatchMode == null)
            return new(false, selection.Code, selection.Message, addonName, null);

        var node = FindNodeByPath(addon, addonName, selection.TargetNodePath);
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
            RenderedUiClickDispatchMode.MouseClick => Dispatch(node, AtkEventType.MouseClick, bounds),
            RenderedUiClickDispatchMode.MouseDownUp =>
                Dispatch(node, AtkEventType.MouseDown, bounds) && Dispatch(node, AtkEventType.MouseUp, bounds),
            RenderedUiClickDispatchMode.MouseDown => Dispatch(node, AtkEventType.MouseDown, bounds),
            _ => false,
        };
        return dispatched
            ? new(true,
                rolloverOnly ? "RenderedTextRollOverDispatched" : "RenderedTextClickDispatched",
                rolloverOnly
                    ? "The registered rollover event was dispatched to the rendered text component."
                    : "The registered click event was dispatched to the rendered text component.",
                addonName,
                selection.TargetNodePath)
            : Fail(
                rolloverOnly ? "RenderedTextRollOverRejected" : "RenderedTextClickRejected",
                rolloverOnly
                    ? "The rendered text component rejected its registered rollover event."
                    : "The rendered text component rejected its registered click event.",
                addonName,
                selection.TargetNodePath);
    }

    private static unsafe bool Dispatch(AtkResNode* node, AtkEventType eventType, FFXIVClientStructs.FFXIV.Common.Math.Bounds bounds)
    {
        var evt = new AtkEventDispatcher.Event
        {
            State = new AtkEventState { EventType = eventType },
            EventData = new AtkEventData
            {
                MouseData = new AtkEventData.AtkMouseData
                {
                    PosX = (short)Math.Clamp((bounds.Pos1.X + bounds.Pos2.X) / 2, short.MinValue, short.MaxValue),
                    PosY = (short)Math.Clamp((bounds.Pos1.Y + bounds.Pos2.Y) / 2, short.MinValue, short.MaxValue),
                },
            },
        };
        return node->DispatchEvent(&evt);
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
}
