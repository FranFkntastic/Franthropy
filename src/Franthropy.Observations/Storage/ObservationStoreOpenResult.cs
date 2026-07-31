namespace Franthropy.Observations.Storage;

public enum ObservationStoreOpenStatus
{
    Ready,
    Missing,
    UpgradeRequired,
    UnsupportedDatabaseVersion,
    NativeSqliteTooOld,
    IncompatibleWriterCapability,
    CorruptDatabase,
    Unavailable,
}

public sealed record ObservationReaderOpenResult(
    ObservationStoreOpenStatus Status,
    SqliteObservationReader? Reader,
    string Message,
    ObservationDatabaseProbeResult Probe)
{
    public bool IsReady => Status == ObservationStoreOpenStatus.Ready && Reader is not null;
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
    public string? BackupDirectory { get; init; }
    public string? MigrationLockPath { get; init; }
    public string? ChangeSignalPath { get; init; }
    public int WriterCapability { get; init; } = 1;
    public TimeSpan BusyTimeout { get; init; } = TimeSpan.FromSeconds(1);
    public Version MinimumNativeSqliteVersion { get; init; } = new(3, 51, 3);
    public long HistorySoftLimitBytes { get; init; } = 256L * 1024 * 1024;

    internal Action? BeforeMigrationCommit { get; init; }
}
