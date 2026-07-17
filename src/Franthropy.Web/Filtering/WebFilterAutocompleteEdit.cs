using Franthropy.Filtering.Completion;

namespace Franthropy.Web.Filtering;

public sealed record WebFilterAutocompleteEdit(string Value, int CaretPosition, bool IsCompletion = false)
{
    public static bool TryApply(
        string? expression,
        FilterCompletionItem completion,
        out WebFilterAutocompleteEdit edit)
    {
        ArgumentNullException.ThrowIfNull(completion);
        var value = expression ?? string.Empty;
        var span = completion.ReplacementSpan;
        if (span.Start < 0 || span.End > value.Length)
        {
            edit = new(value, Math.Clamp(span.Start, 0, value.Length));
            return false;
        }

        var completed = string.Concat(
            value.AsSpan(0, span.Start),
            completion.InsertionText,
            value.AsSpan(span.End));
        edit = new(completed, span.Start + completion.InsertionText.Length, IsCompletion: true);
        return true;
    }
}
