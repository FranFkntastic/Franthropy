namespace Franthropy.Observations.V1;

public enum ObservationWriteStatus
{
    AcceptedChanged,
    AcceptedConfirmed,
    PreservedAsStale,
    IgnoredRepeatedRevision,
    IgnoredOlderRevision,
    Rejected,
    Busy,
    UnsupportedDatabaseVersion,
    Unavailable,
}

public sealed record ObservationWriteResult(
    ObservationWriteStatus Status,
    string Message,
    long? CurrentRevision = null)
{
    public bool ChangedTrustedState => Status is ObservationWriteStatus.AcceptedChanged or ObservationWriteStatus.PreservedAsStale;
}

public enum ObservationReadStatus
{
    Found,
    NotObserved,
    UnsupportedDatabaseVersion,
    Busy,
    Unavailable,
}

public sealed record TrustedObservation(
    long Revision,
    ObservationScope Scope,
    ObservationCapture Capture,
    ObservationPayload Payload,
    bool IsStale,
    string? StaleReason,
    DateTimeOffset? StaleObservedAtUtc,
    DateTimeOffset LastConfirmedAtUtc,
    int ConfirmationCount);

public sealed record ObservationReadResult(
    ObservationReadStatus Status,
    TrustedObservation? Observation,
    string Message);

public enum ObservationChangeKind
{
    Replaced,
    Confirmed,
    MarkedStale,
    Invalidated,
}

public sealed record ObservationChange(
    ObservationScope Scope,
    long Revision,
    ObservationChangeKind Kind,
    DateTimeOffset ChangedAtUtc);

public interface IObservationReader
{
    ValueTask<ObservationReadResult> ReadCurrentAsync(
        ObservationScope scope,
        CancellationToken cancellationToken = default);
}

public interface IObservationWriter
{
    ValueTask<ObservationWriteResult> WriteAsync(
        ObservationEnvelope observation,
        CancellationToken cancellationToken = default);

    ValueTask<ObservationWriteResult> InvalidateAsync(
        ObservationScope scope,
        string reason,
        DateTimeOffset invalidatedAtUtc,
        CancellationToken cancellationToken = default);
}

public interface IObservationStore : IObservationReader, IObservationWriter, IAsyncDisposable
{
    event EventHandler<ObservationChange>? Changed;
}

public enum ObservationLeadershipState
{
    Stopped,
    Reader,
    WaitingForOwnership,
    Collector,
    Faulted,
    Incompatible,
}

public sealed record ObservationLeadershipSnapshot(
    ObservationLeadershipState State,
    string PluginName,
    string PluginInstanceId,
    string FranthropyVersion,
    int WriterCapability,
    string Message);
