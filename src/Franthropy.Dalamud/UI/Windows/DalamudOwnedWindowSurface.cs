using Dalamud.Bindings.ImGui;

namespace Franthropy.Dalamud.UI.Windows;

/// <summary>
/// Captures the ImGui window and viewport that own a popup or companion window.
/// Apply the viewport before beginning the owned window, then restore its display
/// relationship after a successful Begin without changing keyboard focus.
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

        ImGuiP.BringWindowToDisplayBehind(owner, owned);
    }
}
