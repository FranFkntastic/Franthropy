using Dalamud.Bindings.ImGui;

namespace Franthropy.Dalamud.UI.Windows;

/// <summary>
/// Captures the ImGui window and viewport that own a popup or companion window.
/// Apply the viewport before beginning the owned window, then restore its display
/// relationship after a successful Begin without changing keyboard focus or
/// moving the owner backward in the global display order.
/// </summary>
public readonly record struct DalamudOwnedWindowSurface(uint OwnerWindowId, uint OwnerViewportId)
{
    public static unsafe DalamudOwnedWindowSurface Capture()
    {
        var owner = ImGuiP.GetCurrentWindow();
        if (owner.IsNull)
            throw new InvalidOperationException("An owned window surface must be captured inside an active ImGui window.");

        return new DalamudOwnedWindowSurface(owner.ID, ImGui.GetWindowViewport().ID);
    }

    public void ApplyToNextWindow() => ImGui.SetNextWindowViewport(OwnerViewportId);

    public unsafe void KeepCurrentWindowAboveOwner()
    {
        var owner = ImGuiP.FindWindowByID(OwnerWindowId);
        var owned = ImGuiP.GetCurrentWindow();
        if (owner.IsNull || owned.IsNull || owner.ID == owned.ID)
            return;

        // Moving the owner behind the owned window is not equivalent when the
        // owner belongs to a dock tree: if another root window already sits
        // between them, that operation drags the entire owner beneath it and
        // hands both rendering and hit-testing to the unrelated window.
        // Promote only the transient surface. This preserves the owner's
        // existing place while keeping the popup/companion visible.
        ImGuiP.BringWindowToDisplayFront(owned);
    }
}
