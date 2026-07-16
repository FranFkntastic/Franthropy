using System.Text;
using Franthropy.Filtering.Diagnostics;

namespace Franthropy.Filtering.Syntax;

internal sealed class FilterTokenizer(string source, FilterLimits limits, DiagnosticBag diagnostics)
{
    private readonly List<FilterToken> tokens = [];
    private int position;
    private bool exceededTokenLimit;

    public IReadOnlyList<FilterToken> Tokenize()
    {
        while (position < source.Length && !exceededTokenLimit)
        {
            var hadWhitespace = SkipWhitespace();
            if (position >= source.Length)
                break;

            if (tokens.Count >= limits.MaximumTokenCount - 1)
            {
                diagnostics.Add(
                    FilterDiagnosticCodes.TooManyTokens,
                    $"This filter contains more than {limits.MaximumTokenCount:N0} tokens.",
                    TextSpan.EmptyAt(position));
                exceededTokenLimit = true;
                break;
            }

            tokens.Add(ReadToken(hadWhitespace));
        }

        tokens.Add(new FilterToken(
            FilterTokenKind.EndOfFile,
            string.Empty,
            string.Empty,
            TextSpan.EmptyAt(Math.Min(position, source.Length)),
            position > 0 && char.IsWhiteSpace(source[Math.Min(position, source.Length) - 1])));
        return tokens;
    }

    private FilterToken ReadToken(bool hadWhitespace)
    {
        var start = position;
        var current = source[position];
        switch (current)
        {
            case '(':
                position++;
                return Token(FilterTokenKind.LeftParenthesis, start, hadWhitespace);
            case ')':
                position++;
                return Token(FilterTokenKind.RightParenthesis, start, hadWhitespace);
            case ':':
                position++;
                return Token(FilterTokenKind.Colon, start, hadWhitespace);
            case '=':
                position++;
                return Token(FilterTokenKind.Equals, start, hadWhitespace);
            case '!':
                position++;
                if (Match('='))
                    return Token(FilterTokenKind.BangEquals, start, hadWhitespace);
                return Token(FilterTokenKind.Bang, start, hadWhitespace);
            case '<':
                position++;
                if (Match('='))
                    return Token(FilterTokenKind.LessOrEqual, start, hadWhitespace);
                return Token(FilterTokenKind.Less, start, hadWhitespace);
            case '>':
                position++;
                if (Match('='))
                    return Token(FilterTokenKind.GreaterOrEqual, start, hadWhitespace);
                return Token(FilterTokenKind.Greater, start, hadWhitespace);
            case '.':
                if (Peek(1) == '.')
                {
                    position += 2;
                    return Token(FilterTokenKind.DotDot, start, hadWhitespace);
                }

                return ReadWord(hadWhitespace);
            case '|':
                position++;
                if (Match('|'))
                {
                    var text = source[start..position];
                    return new FilterToken(FilterTokenKind.Pipe, text, text, TextSpan.FromBounds(start, position), hadWhitespace);
                }

                return Token(FilterTokenKind.Pipe, start, hadWhitespace);
            case '&':
                position++;
                if (Match('&'))
                    return Token(FilterTokenKind.AmpersandAmpersand, start, hadWhitespace);

                diagnostics.Add(
                    FilterDiagnosticCodes.UnexpectedCharacter,
                    "Use '&&' for AND; a single '&' is not a filter operator.",
                    TextSpan.FromBounds(start, position));
                return Token(FilterTokenKind.Bad, start, hadWhitespace);
            case '-':
                position++;
                return Token(FilterTokenKind.Minus, start, hadWhitespace);
            case '"':
                return ReadQuotedString(hadWhitespace);
            default:
                return ReadWord(hadWhitespace);
        }
    }

    private FilterToken ReadQuotedString(bool hadWhitespace)
    {
        var start = position++;
        var value = new StringBuilder();
        var terminated = false;

        while (position < source.Length)
        {
            var current = source[position++];
            if (current == '"')
            {
                terminated = true;
                break;
            }

            if (current != '\\')
            {
                value.Append(current);
                continue;
            }

            if (position >= source.Length)
            {
                diagnostics.Add(
                    FilterDiagnosticCodes.InvalidEscape,
                    "A quoted value cannot end with an escape character.",
                    TextSpan.FromBounds(position - 1, position));
                break;
            }

            var escaped = source[position++];
            if (escaped is '"' or '\\')
            {
                value.Append(escaped);
                continue;
            }

            diagnostics.Add(
                FilterDiagnosticCodes.InvalidEscape,
                $"'\\{escaped}' is not a valid escape. Only escaped quotes and backslashes are supported.",
                TextSpan.FromBounds(position - 2, position));
            value.Append(escaped);
        }

        if (!terminated)
        {
            diagnostics.Add(
                FilterDiagnosticCodes.UnterminatedString,
                "Expected a closing quote.",
                TextSpan.FromBounds(start, position));
        }

        return new FilterToken(
            FilterTokenKind.QuotedString,
            source[start..position],
            value.ToString(),
            TextSpan.FromBounds(start, position),
            hadWhitespace);
    }

    private FilterToken ReadWord(bool hadWhitespace)
    {
        var start = position;
        while (position < source.Length && !IsWordBoundary(position))
            position++;

        if (position == start)
        {
            position++;
            diagnostics.Add(
                FilterDiagnosticCodes.UnexpectedCharacter,
                $"'{source[start]}' is not valid filter syntax.",
                TextSpan.FromBounds(start, position));
            return Token(FilterTokenKind.Bad, start, hadWhitespace);
        }

        var text = source[start..position];
        var kind = text.ToUpperInvariant() switch
        {
            "AND" => FilterTokenKind.AndKeyword,
            "OR" => FilterTokenKind.OrKeyword,
            "NOT" => FilterTokenKind.NotKeyword,
            _ => FilterTokenKind.Word,
        };
        return new FilterToken(kind, text, text, TextSpan.FromBounds(start, position), hadWhitespace);
    }

    private bool IsWordBoundary(int index)
    {
        var current = source[index];
        if (char.IsWhiteSpace(current))
            return true;
        if (current is '(' or ')' or ':' or '=' or '!' or '<' or '>' or '|' or '&' or '-' or '"')
            return true;
        return current == '.' && Peek(index - position + 1) == '.';
    }

    private bool SkipWhitespace()
    {
        var start = position;
        while (position < source.Length && char.IsWhiteSpace(source[position]))
            position++;
        return position > start;
    }

    private bool Match(char expected)
    {
        if (position >= source.Length || source[position] != expected)
            return false;
        position++;
        return true;
    }

    private char Peek(int offset)
    {
        var index = position + offset;
        return index >= 0 && index < source.Length ? source[index] : '\0';
    }

    private FilterToken Token(FilterTokenKind kind, int start, bool hadWhitespace)
    {
        var text = source[start..position];
        return new FilterToken(kind, text, text, TextSpan.FromBounds(start, position), hadWhitespace);
    }
}
