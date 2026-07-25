namespace Franthropy.Dalamud.UI.Windows;

/// <summary>
/// Tracks the one-frame focus request required when a companion window is opened
/// from another ImGui window. Keeping this state explicit prevents a newly opened
/// companion from remaining behind the window that launched it.
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
