using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Franthropy.Dalamud.UI.Windows;

/// <summary>Begins popups and modals that stay on their owner's viewport, above the owner, with an opaque background.</summary>
/// <remarks>
/// Call from inside the owning window, where <see cref="DalamudOwnedWindowSurface.Capture"/> is valid.
/// Each Begin that returns true must be paired with <c>ImGui.EndPopup</c>. Many game themes give
/// <see cref="ImGuiCol.PopupBg"/> a translucent alpha, which lets the content behind a popup bleed through it;
/// these helpers override the alpha for the popup window only.
/// </remarks>
public static class DalamudOwnedPopup
{
    /// <summary>Begins a popup opened earlier with <c>ImGui.OpenPopup</c>.</summary>
    /// <param name="id">Popup ID, as passed to <c>ImGui.OpenPopup</c> in the same ID scope.</param>
    /// <param name="flags">Popup window flags.</param>
    /// <param name="backgroundAlpha">Window background alpha; 1 is fully opaque.</param>
    /// <remarks>Position and size constraints set with <c>SetNextWindow*</c> before this call still apply.</remarks>
    public static bool BeginOpaquePopup(string id, ImGuiWindowFlags flags = ImGuiWindowFlags.None, float backgroundAlpha = 1f)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var owner = DalamudOwnedWindowSurface.Capture();
        owner.ApplyToNextWindow();
        ImGui.SetNextWindowBgAlpha(backgroundAlpha);
        if (!ImGui.BeginPopup(id, flags))
            return false;

        owner.KeepCurrentWindowAboveOwner();
        return true;
    }

    /// <summary>Begins a modal without a title-bar close button, centred on the owning window when it appears.</summary>
    public static bool BeginOwnedModal(string id, ImGuiWindowFlags flags = ImGuiWindowFlags.AlwaysAutoResize)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var owner = PrepareModal();
        if (!ImGui.BeginPopupModal(id, flags))
            return false;

        owner.KeepCurrentWindowAboveOwner();
        return true;
    }

    /// <summary>Begins a modal with a title-bar close button, centred on the owning window when it appears.</summary>
    /// <param name="id">Modal ID, as passed to <c>ImGui.OpenPopup</c> in the same ID scope.</param>
    /// <param name="open">Set to false when the user closes the modal from its title bar.</param>
    /// <param name="flags">Modal window flags.</param>
    public static bool BeginOwnedModal(string id, ref bool open, ImGuiWindowFlags flags = ImGuiWindowFlags.AlwaysAutoResize)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var owner = PrepareModal();
        if (!ImGui.BeginPopupModal(id, ref open, flags))
            return false;

        owner.KeepCurrentWindowAboveOwner();
        return true;
    }

    private static DalamudOwnedWindowSurface PrepareModal()
    {
        // The owner can live in its own OS viewport; a modal left on the game's viewport would
        // open behind it and capture input while invisible.
        var owner = DalamudOwnedWindowSurface.Capture();
        owner.ApplyToNextWindow();
        ImGui.SetNextWindowPos(
            ImGui.GetWindowPos() + (ImGui.GetWindowSize() * 0.5f),
            ImGuiCond.Appearing,
            new Vector2(0.5f, 0.5f));
        ImGui.SetNextWindowBgAlpha(1f);
        return owner;
    }
}
