namespace Franthropy.Filtering.Evaluation;

public readonly record struct FieldEvidence<T>
{
    private readonly T? value;

    private FieldEvidence(bool isKnown, T? value, string? unknownReason)
    {
        IsKnown = isKnown;
        this.value = value;
        UnknownReason = unknownReason;
    }

    public bool IsKnown { get; }
    public string? UnknownReason { get; }

    public T Value => IsKnown
        ? value!
        : throw new InvalidOperationException("Unknown field evidence has no value.");

    public static FieldEvidence<T> Known(T value) => new(true, value, null);

    public static FieldEvidence<T> Unknown(string reason) =>
        new(false, default, string.IsNullOrWhiteSpace(reason) ? "The value was not observed." : reason.Trim());
}

public static class Evidence
{
    public static FieldEvidence<T> Known<T>(T value) => FieldEvidence<T>.Known(value);

    public static FieldEvidence<T> Unknown<T>(string reason) => FieldEvidence<T>.Unknown(reason);
}
