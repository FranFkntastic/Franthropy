using Franthropy.Filtering.Completion;

namespace Franthropy.Web.Filtering;

public sealed record WebFilterAutocompleteEdit(string Value, int CaretPosition, bool IsCompletion = false)
{
    public static bool TryApply(
        string? expression,
        FilterCompletionItem completion,
        out WebFilterAutocompleteEdit edit)
    {
        if (!FilterCompletionEdit.TryApply(expression, completion, out var coreEdit))
        {
            edit = new(coreEdit.Value, coreEdit.CaretPosition);
            return false;
        }

        edit = new(coreEdit.Value, coreEdit.CaretPosition, coreEdit.IsCompletion);
        return true;
    }
}
