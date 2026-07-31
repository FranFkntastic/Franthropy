namespace Franthropy.Observations.Storage;

public enum ObservationStoreOpenStatus
{
    Ready,
    UnsupportedDatabaseVersion,
    NativeSqliteTooOld,
    CorruptDatabase,
    Unavailable,
}

public sealed record ObservationStoreOpenResult(
    ObservationStoreOpenStatus Status,
    SqliteObservationStore? Store,
    string Message,
    Version? NativeSqliteVersion = null)
{
    public bool IsReady => Status == ObservationStoreOpenStatus.Ready && Store is not null;
}

public sealed record ObservationStoreOptions
{
    public required string DatabasePath { get; init; }
    public TimeSpan BusyTimeout { get; init; } = TimeSpan.FromSeconds(1);
    public Version MinimumNativeSqliteVersion { get; init; } = new(3, 51, 3);
    public long HistorySoftLimitBytes { get; init; } = 256L * 1024 * 1024;
}
