namespace Franthropy.Dalamud.UI.Inputs;

/// <summary>Parses signed decimal integers with optional grouping separators.</summary>
public static class GroupedIntegerParser
{
    /// <summary>Returns false for empty/invalid text; otherwise clamps overflow to the Int32 range.</summary>
    public static bool TryParse(string? text, out int value)
    {
        var valid = TryParse(text, out long parsed);
        value = (int)Math.Clamp(parsed, int.MinValue, int.MaxValue);
        return valid;
    }

    /// <summary>
    /// Ignores commas, underscores, ASCII spaces, thin spaces (U+2009), non-breaking
    /// spaces (U+00A0), and narrow non-breaking spaces (U+202F). After removing those,
    /// accepts an optional leading sign and ASCII digits only. Overflow saturates;
    /// invalid input returns false with a zero output, which callers must not commit.
    /// </summary>
    public static bool TryParse(string? text, out long value)
    {
        value = 0;
        if (text is null) return false;
        var negative = false;
        var started = false;
        var hasDigit = false;
        ulong magnitude = 0;
        var limit = (ulong)long.MaxValue;
        foreach (var character in text)
        {
            if (IsSeparator(character)) continue;
            if (!started && character is '+' or '-')
            {
                started = true;
                negative = character == '-';
                if (negative) limit++;
                continue;
            }
            if (character is < '0' or > '9') return false;
            started = hasDigit = true;
            var digit = (uint)(character - '0');
            magnitude = magnitude > (limit - digit) / 10 ? limit : magnitude * 10 + digit;
        }
        if (!hasDigit) return false;
        value = negative
            ? magnitude == (ulong)long.MaxValue + 1 ? long.MinValue : -(long)magnitude
            : (long)magnitude;
        return true;
    }

    internal static bool IsSeparator(char character) =>
        character is ',' or '_' or ' ' or '\u2009' or '\u00a0' or '\u202f';
}
