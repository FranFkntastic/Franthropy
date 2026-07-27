namespace Franthropy.Dalamud.UI.Windows;

/// <summary>
/// Tracks the one-frame keyboard-focus request made when a companion window is
/// opened from another ImGui window. Display ownership is maintained separately
/// by <see cref="DalamudOwnedWindowSurface"/>.
/// </summary>
public sealed class CompanionWindowActivationState
{
    public bool FocusRequested { get; private set; }

    public bool Toggle(bool isOpen)
    {
        if (isOpen)
        {
            FocusRequested = false;
            return false;
        }

        FocusRequested = true;
        return true;
    }

    public void RequestOpen() => FocusRequested = true;

    public bool ConsumeFocusRequest()
    {
        if (!FocusRequested)
            return false;

        FocusRequested = false;
        return true;
    }
}
