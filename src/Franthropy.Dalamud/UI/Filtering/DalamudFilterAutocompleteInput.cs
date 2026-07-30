namespace Franthropy.Dalamud.UI.Filtering;

public readonly record struct DalamudFilterAutocompleteInputDecision(
    int SelectionDelta,
    bool ApplyCompletion,
    bool KeepInputFocused);

public static class DalamudFilterAutocompleteInput
{
    public static DalamudFilterAutocompleteInputDecision Decide(
        bool inputActive,
        int suggestionCount,
        bool downPressed,
        bool upPressed,
        bool tabPressed)
    {
        if (!inputActive)
            return default;

        var selectionDelta = (downPressed ? 1 : 0) - (upPressed ? 1 : 0);
        return new DalamudFilterAutocompleteInputDecision(
            selectionDelta,
            tabPressed && suggestionCount > 0,
            tabPressed);
    }
}
