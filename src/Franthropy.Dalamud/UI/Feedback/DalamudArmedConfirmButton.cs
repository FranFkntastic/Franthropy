using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Franthropy.Dalamud.UI.Styling;

namespace Franthropy.Dalamud.UI.Feedback;

/// <summary>
/// A press-twice button for destructive actions: the first press arms it for one target, the second
/// press within the arm window confirms. Lighter than a modal for actions that are also undoable.
/// </summary>
/// <remarks>
/// One instance can serve many targets (for example, a delete button per row); only one target is
/// armed at a time. The arm drops when its window elapses, when the user clicks anywhere else or
/// presses Escape, when another target is pressed, and when no button was drawn on the previous
/// frame (the surface was hidden). External invokers, such as an agent bridge, call
/// <see cref="Press"/> and get the same two-step behaviour.
/// </remarks>
public sealed class DalamudArmedConfirmButton<TKey>
{
    private readonly TimeSpan window;
    private readonly TimeProvider timeProvider;
    private readonly IEqualityComparer<TKey> comparer;
    private bool armed;
    private TKey? armedKey;
    private DateTimeOffset armedAt;
    private int lastDrawFrame = -1;

    /// <param name="window">How long the arm lasts; 4 seconds when null.</param>
    /// <param name="timeProvider">Clock for the arm window; the system clock when null.</param>
    /// <param name="comparer">Target equality; the default comparer when null.</param>
    public DalamudArmedConfirmButton(
        TimeSpan? window = null,
        TimeProvider? timeProvider = null,
        IEqualityComparer<TKey>? comparer = null)
    {
        this.window = window ?? TimeSpan.FromSeconds(4);
        if (this.window <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(window), "The arm window must be positive.");
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.comparer = comparer ?? EqualityComparer<TKey>.Default;
    }

    /// <summary>Whether <paramref name="key"/> is armed and its window has not elapsed.</summary>
    public bool IsArmed(TKey key) => TryGetArmedKey(out var current) && comparer.Equals(current, key);

    /// <summary>The armed target, if any arm is still live.</summary>
    public bool TryGetArmedKey([MaybeNullWhen(false)] out TKey key)
    {
        if (armed && timeProvider.GetUtcNow() - armedAt > window)
            Disarm();
        key = armedKey;
        return armed;
    }

    /// <summary>Arms <paramref name="key"/>, or confirms it when it is already armed.</summary>
    /// <returns>True when this press confirms the action; the button is then disarmed.</returns>
    public bool Press(TKey key)
    {
        if (IsArmed(key))
        {
            Disarm();
            return true;
        }

        armed = true;
        armedKey = key;
        armedAt = timeProvider.GetUtcNow();
        return false;
    }

    public void Disarm()
    {
        armed = false;
        armedKey = default;
    }

    /// <summary>A short state description for review tooling, e.g. an agent bridge control value.</summary>
    public string DescribeState(TKey key) =>
        IsArmed(key) ? "Armed: invoke again to confirm" : "Invoke once to arm, again to confirm";

    /// <summary>Draws the button for <paramref name="key"/>, red and relabelled while armed.</summary>
    /// <param name="id">Stable ID for this target's button; keeps the button's identity across the label change.</param>
    /// <param name="key">The target this button acts on.</param>
    /// <param name="label">Label while disarmed, e.g. "Delete".</param>
    /// <param name="armedLabel">Label while armed, e.g. "Delete?".</param>
    /// <param name="enabled">A disabled button neither arms nor shows an existing arm.</param>
    /// <param name="size">Button size; auto-fit when zero.</param>
    /// <returns>True on the confirming press.</returns>
    /// <remarks>To wrap the button with its row, measure the label it will show:
    /// <c>SameLineIfFits(button.IsArmed(key) ? armedLabel : label)</c>.</remarks>
    public bool Draw(string id, TKey key, string label, string armedLabel, bool enabled = true, Vector2 size = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentException.ThrowIfNullOrWhiteSpace(armedLabel);

        var frame = ImGui.GetFrameCount();
        if (frame != lastDrawFrame && lastDrawFrame != frame - 1)
            Disarm();
        lastDrawFrame = frame;

        var isArmed = enabled && IsArmed(key);
        bool clicked;
        ImGui.BeginDisabled(!enabled);
        using (isArmed ? DalamudUiChrome.PushDangerButton() : null)
            clicked = ImGui.Button($"{(isArmed ? armedLabel : label)}###{id}", size);
        ImGui.EndDisabled();
        var hovered = ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled);

        if (clicked && enabled)
            return Press(key);

        if (isArmed &&
            (ImGui.IsKeyPressed(ImGuiKey.Escape, false) ||
             (!hovered && (ImGui.IsMouseClicked(ImGuiMouseButton.Left) || ImGui.IsMouseClicked(ImGuiMouseButton.Right)))))
            Disarm();
        return false;
    }
}
