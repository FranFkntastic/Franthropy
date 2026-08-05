using System.Globalization;
using Franthropy.Observations.V1;
using Microsoft.Data.Sqlite;

namespace Franthropy.Observations.Storage;

public enum ObservationDatabaseProbeStatus
{
    Missing,
    Compatible,
    UpgradeRequired,
    UnsupportedDatabaseVersion,
    NativeSqliteTooOld,
    Busy,
    CorruptDatabase,
    Unavailable,
}

public sealed record ObservationDatabaseProbeResult(
    ObservationDatabaseProbeStatus Status,
    ObservationVersion? SchemaVersion,
    ObservationVersion? ContractVersion,
    int MinimumWriterCapability,
    long CurrentRevision,
    string Message,
    Version? NativeSqliteVersion = null)
{
    public bool CanWrite(int writerCapability) =>
        Status is (ObservationDatabaseProbeStatus.Missing or ObservationDatabaseProbeStatus.Compatible or ObservationDatabaseProbeStatus.UpgradeRequired) &&
        writerCapability >= MinimumWriterCapability;
}

public static class ObservationDatabaseProbe
{
    private const int SqliteBusy = 5;
    private const int SqliteLocked = 6;

    public static async ValueTask<ObservationDatabaseProbeResult> ReadAsync(
        ObservationStoreOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var path = Path.GetFullPath(options.DatabasePath);
        if (!File.Exists(path) || new FileInfo(path).Length == 0)
        {
            return new ObservationDatabaseProbeResult(
                ObservationDatabaseProbeStatus.Missing,
                null,
                null,
                2,
                0,
                "No shared observation database exists yet.");
        }

        try
        {
            SQLitePCL.Batteries_V2.Init();
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                // A read-only shared cache can survive the probe connection and cause the
                // elected collector's subsequent writer connection to inherit read-only state.
                // The probe is diagnostic and must never participate in the writer's cache.
                Cache = SqliteCacheMode.Private,
                Pooling = false,
                DefaultTimeout = Math.Max(1, (int)Math.Ceiling(options.BusyTimeout.TotalSeconds)),
            };
            await using var connection = new SqliteConnection(builder.ToString());
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            var nativeText = await ScalarAsync(connection, "SELECT sqlite_version();", cancellationToken).ConfigureAwait(false);
            var nativeVersion = Version.TryParse(nativeText, out var parsed) ? parsed : new Version(0, 0);
            if (nativeVersion < options.MinimumNativeSqliteVersion)
            {
                return Result(ObservationDatabaseProbeStatus.NativeSqliteTooOld, null, null, 1, 0,
                    $"Loaded SQLite {nativeVersion} is older than required {options.MinimumNativeSqliteVersion}.", nativeVersion);
            }

            var integrity = await ScalarAsync(connection, "PRAGMA quick_check;", cancellationToken).ConfigureAwait(false);
            if (!string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase))
                return Result(ObservationDatabaseProbeStatus.CorruptDatabase, null, null, 1, 0, $"SQLite quick_check failed: {integrity}", nativeVersion);

            var userVersion = Convert.ToInt32(await ScalarAsync(connection, "PRAGMA user_version;", cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
            if (userVersion is not 1000 and not 1001 and not 1002)
                return Result(ObservationDatabaseProbeStatus.UnsupportedDatabaseVersion, null, null, int.MaxValue, 0,
                    $"Database schema user_version {userVersion} is unsupported.", nativeVersion);

            var metadata = await ReadMetadataAsync(connection, cancellationToken).ConfigureAwait(false);
            var schema = new ObservationVersion(ReadInt(metadata, "schema_major"), ReadInt(metadata, "schema_minor"));
            var contract = new ObservationVersion(ReadInt(metadata, "contract_major"), ReadInt(metadata, "contract_minor"));
            var minimumWriter = ReadInt(metadata, "minimum_writer_capability");
            var revision = ReadLong(metadata, "next_revision");
            if (schema.Major != ObservationContract.SchemaVersion.Major || contract.Major != ObservationContract.Version.Major)
                return Result(ObservationDatabaseProbeStatus.UnsupportedDatabaseVersion, schema, contract, minimumWriter, revision,
                    $"Database schema {schema} and contract {contract} are not supported.", nativeVersion);

            var status = schema.Minor < ObservationContract.SchemaVersion.Minor
                ? ObservationDatabaseProbeStatus.UpgradeRequired
                : schema.Minor == ObservationContract.SchemaVersion.Minor
                    ? ObservationDatabaseProbeStatus.Compatible
                    : ObservationDatabaseProbeStatus.UnsupportedDatabaseVersion;
            return Result(status, schema, contract, minimumWriter, revision,
                status == ObservationDatabaseProbeStatus.Compatible ? "The shared observation database is compatible." :
                status == ObservationDatabaseProbeStatus.UpgradeRequired ? $"Database schema {schema} requires a forward migration to {ObservationContract.SchemaVersion}." :
                $"Database schema {schema} is newer than supported {ObservationContract.SchemaVersion}.", nativeVersion);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is SqliteBusy or SqliteLocked)
        {
            return Result(ObservationDatabaseProbeStatus.Busy, null, null, int.MaxValue, 0, "The shared observation database is busy.");
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is 11 or 26)
        {
            return Result(ObservationDatabaseProbeStatus.CorruptDatabase, null, null, int.MaxValue, 0, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException or FormatException)
        {
            return Result(ObservationDatabaseProbeStatus.Unavailable, null, null, int.MaxValue, 0, ex.Message);
        }
    }

    private static ObservationDatabaseProbeResult Result(
        ObservationDatabaseProbeStatus status,
        ObservationVersion? schema,
        ObservationVersion? contract,
        int minimumWriter,
        long revision,
        string message,
        Version? native = null) => new(status, schema, contract, minimumWriter, revision, message, native);

    private static async ValueTask<Dictionary<string, string>> ReadMetadataAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT key, value FROM observation_metadata;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result[reader.GetString(0)] = reader.GetString(1);
        return result;
    }

    private static int ReadInt(IReadOnlyDictionary<string, string> values, string key) =>
        int.Parse(values[key], CultureInfo.InvariantCulture);

    private static long ReadLong(IReadOnlyDictionary<string, string> values, string key) =>
        long.Parse(values[key], CultureInfo.InvariantCulture);

    private static async ValueTask<string> ScalarAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) ?? string.Empty;
    }
}
