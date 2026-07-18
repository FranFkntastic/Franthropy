using Franthropy.Filtering.Completion;

namespace Franthropy.Dalamud.UI.Filtering;

public sealed class DalamudFilterAutocompleteState
{
    public string Expression { get; private set; } = string.Empty;
    public int CaretPosition { get; internal set; }
    public int SelectedIndex { get; private set; }
    internal int? PendingCaretPosition { get; private set; }
    internal bool FocusRequested { get; private set; }

    public void SetExpression(string? expression, int? caretPosition = null)
    {
        Expression = expression ?? string.Empty;
        CaretPosition = Math.Clamp(caretPosition ?? Expression.Length, 0, Expression.Length);
        SelectedIndex = 0;
    }

    public void MoveSelection(int delta, int itemCount)
    {
        if (itemCount <= 0)
        {
            SelectedIndex = 0;
            return;
        }

        SelectedIndex = (SelectedIndex + delta) % itemCount;
        if (SelectedIndex < 0)
            SelectedIndex += itemCount;
    }

    public void RequestFocus()
    {
        PendingCaretPosition = CaretPosition;
        FocusRequested = true;
    }

    public bool TryApply(IReadOnlyList<FilterCompletionItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0)
            return false;

        SelectedIndex = Math.Clamp(SelectedIndex, 0, items.Count - 1);
        if (!FilterCompletionEdit.TryApply(Expression, items[SelectedIndex], out var edit))
            return false;

        Expression = edit.Value;
        CaretPosition = edit.CaretPosition;
        PendingCaretPosition = edit.CaretPosition;
        FocusRequested = true;
        SelectedIndex = 0;
        return true;
    }

    internal void ConsumePendingCaret()
    {
        PendingCaretPosition = null;
        FocusRequested = false;
    }
}
