using System.Text;
using System.Text.RegularExpressions;

namespace Franthropy.Dalamud.UI.Performance;

public sealed record RenderLoopPolicyViolation(
    string SourceName,
    string MethodName,
    int Line,
    string Message);

/// <summary>
/// Test-time policy for consumer source trees. Render methods may delegate iteration to a
/// virtualized primitive; a remaining direct loop must declare why it is bounded and its maximum
/// expected iterations with <see cref="RenderFrameWorkJustificationAttribute"/>.
/// </summary>
public static partial class RenderLoopSourcePolicy
{
    public static IReadOnlyList<RenderLoopPolicyViolation> Analyze(
        string source,
        string sourceName = "source")
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);
        var masked = MaskSource(source, maskLiterals: true);
        var commentMasked = MaskSource(source, maskLiterals: false);
        var violations = new List<RenderLoopPolicyViolation>();
        foreach (Match method in DrawMethodPattern().Matches(masked))
        {
            var openBrace = masked.IndexOf('{', method.Index);
            if (openBrace < 0)
                continue;
            var closeBrace = FindClosingBrace(masked, openBrace);
            if (closeBrace < 0)
                continue;
            var body = masked[openBrace..(closeBrace + 1)];
            var loop = LoopPattern().Match(body);
            if (!loop.Success || HasJustification(commentMasked, method.Index))
                continue;

            var loopOffset = openBrace + loop.Index;
            violations.Add(new(
                sourceName,
                method.Groups[1].Value,
                1 + source.AsSpan(0, loopOffset).Count('\n'),
                "Render loops must move iteration into a virtualized Franthropy primitive or declare " +
                "[RenderFrameWorkJustification(\"concrete bounded reason\", maximumIterations)]."));
        }
        return violations;
    }

    private static bool HasJustification(string source, int methodIndex)
    {
        var start = Math.Max(0, methodIndex - 500);
        var prefix = source[start..methodIndex];
        var match = JustificationPattern().Match(prefix);
        if (!match.Success)
            return false;
        return match.Groups[1].Value.Trim().Length >= 12 &&
               int.TryParse(match.Groups[2].Value, out var maximum) &&
               maximum > 0;
    }

    private static int FindClosingBrace(string source, int openBrace)
    {
        var depth = 0;
        for (var index = openBrace; index < source.Length; index++)
        {
            if (source[index] == '{') depth++;
            else if (source[index] == '}' && --depth == 0) return index;
        }
        return -1;
    }

    private static string MaskSource(string source, bool maskLiterals)
    {
        var result = new StringBuilder(source);
        var state = LexicalState.Code;
        for (var index = 0; index < source.Length; index++)
        {
            var current = source[index];
            var next = index + 1 < source.Length ? source[index + 1] : '\0';
            switch (state)
            {
                case LexicalState.Code when current == '/' && next == '/':
                    result[index] = result[index + 1] = ' ';
                    index++;
                    state = LexicalState.LineComment;
                    break;
                case LexicalState.Code when current == '/' && next == '*':
                    result[index] = result[index + 1] = ' ';
                    index++;
                    state = LexicalState.BlockComment;
                    break;
                case LexicalState.Code when current == '"':
                    if (maskLiterals) result[index] = ' ';
                    state = LexicalState.String;
                    break;
                case LexicalState.Code when current == '\'':
                    if (maskLiterals) result[index] = ' ';
                    state = LexicalState.Character;
                    break;
                case LexicalState.LineComment:
                    if (current == '\n') state = LexicalState.Code;
                    else result[index] = ' ';
                    break;
                case LexicalState.BlockComment:
                    if (current == '*' && next == '/')
                    {
                        result[index] = result[index + 1] = ' ';
                        index++;
                        state = LexicalState.Code;
                    }
                    else if (current != '\n') result[index] = ' ';
                    break;
                case LexicalState.String:
                    if (current == '\\' && next != '\0')
                    {
                        if (maskLiterals) result[index] = result[index + 1] = ' ';
                        index++;
                    }
                    else if (current == '"')
                    {
                        if (maskLiterals) result[index] = ' ';
                        state = LexicalState.Code;
                    }
                    else if (maskLiterals && current != '\n') result[index] = ' ';
                    break;
                case LexicalState.Character:
                    if (current == '\\' && next != '\0')
                    {
                        if (maskLiterals) result[index] = result[index + 1] = ' ';
                        index++;
                    }
                    else if (current == '\'')
                    {
                        if (maskLiterals) result[index] = ' ';
                        state = LexicalState.Code;
                    }
                    else if (maskLiterals && current != '\n') result[index] = ' ';
                    break;
            }
        }
        return result.ToString();
    }

    private enum LexicalState { Code, LineComment, BlockComment, String, Character }

    [GeneratedRegex(@"\b(?:public|private|protected|internal)\s+(?:(?:static|unsafe|override|virtual|sealed|async)\s+)*[\w<>,?\[\]]+\s+(Draw\w*)\s*\(")]
    private static partial Regex DrawMethodPattern();

    [GeneratedRegex(@"\b(?:for|foreach|while|do)\s*(?:\(|\{)")]
    private static partial Regex LoopPattern();

    [GeneratedRegex("""\[\s*(?:RenderFrameWorkJustification|RenderFrameWorkJustificationAttribute)\s*\(\s*"([^"]+)"\s*,\s*(\d+)\s*\)\s*\]\s*(?:\[[^\]]+\]\s*)*$""")]
    private static partial Regex JustificationPattern();
}
