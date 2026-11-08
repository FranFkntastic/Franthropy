namespace Franthropy.Dalamud.UI.Inputs;

public sealed record DalamudSuggestion<T>(T Value, string Label, string? Note = null);

/// <summary>Selection state for caller-ranked suggestions; owns no search policy.</summary>
public sealed class DalamudSuggestionInputState
{
    public string Text { get; set; } = string.Empty;
    public int HighlightedIndex { get; private set; }
    public bool Active { get; set; }
    internal bool Dismissed { get; set; }

    public void ResetSelection() => HighlightedIndex = 0;
    public void Clamp(int count) => HighlightedIndex = Math.Clamp(HighlightedIndex, 0, Math.Max(0, count - 1));

    public void Move(int delta, int count)
    {
        Clamp(count);
        if (count == 0) return;
        HighlightedIndex = (int)(((long)HighlightedIndex + delta) % count);
        if (HighlightedIndex < 0) HighlightedIndex += count;
    }

    public bool TryPick<T>(IReadOnlyList<DalamudSuggestion<T>> suggestions, out T? picked)
    {
        ArgumentNullException.ThrowIfNull(suggestions);
        Clamp(suggestions.Count);
        picked = default;
        if (suggestions.Count == 0) return false;
        picked = suggestions[HighlightedIndex].Value;
        Text = string.Empty;
        HighlightedIndex = 0;
        Active = false;
        return true;
    }
}
