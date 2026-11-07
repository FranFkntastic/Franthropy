namespace Franthropy.Observations.Storage;

/// <summary>Per-call diagnostic context; never shared between concurrent writers.</summary>
public sealed class ObservationWriteProgress : IDisposable
{
    private static readonly AsyncLocal<ObservationWriteProgress?> Current = new();
    private readonly ObservationWriteProgress? previous;
    private string stage = "dequeued";
    public string Stage => Volatile.Read(ref stage);

    public ObservationWriteProgress()
    {
        previous = Current.Value;
        Current.Value = this;
    }

    public void SetStage(string value) => Volatile.Write(ref stage, value);
    internal static void Mark(string value) => Current.Value?.SetStage(value);
    public void Dispose() => Current.Value = previous;
}
