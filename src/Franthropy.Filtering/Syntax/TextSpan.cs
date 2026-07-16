namespace Franthropy.Filtering.Syntax;

public readonly record struct TextSpan(int Start, int Length)
{
    public int End => checked(Start + Length);

    public static TextSpan EmptyAt(int position) => new(position, 0);

    public static TextSpan FromBounds(int start, int end) =>
        new(start, Math.Max(0, end - start));

    public TextSpan Union(TextSpan other) =>
        FromBounds(Math.Min(Start, other.Start), Math.Max(End, other.End));
}
