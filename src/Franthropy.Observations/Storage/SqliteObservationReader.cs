using System.Globalization;
using System.Text.Json;
using Franthropy.Observations.V1;
using Microsoft.Data.Sqlite;

namespace Franthropy.Observations.Storage;

public sealed class SqliteObservationReader : IObservationReader, IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ObservationStoreOptions options;
    private bool disposed;

    private SqliteObservationReader(ObservationStoreOptions options) => this.options = options;

    public static async ValueTask<ObservationReaderOpenResult> OpenAsync(
        ObservationStoreOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var normalized = options with { DatabasePath = Path.GetFullPath(options.DatabasePath) };
        var probe = await ObservationDatabaseProbe.ReadAsync(normalized, cancellationToken).ConfigureAwait(false);
        return probe.Status switch
        {
            ObservationDatabaseProbeStatus.Compatible or ObservationDatabaseProbeStatus.UpgradeRequired =>
                new ObservationReaderOpenResult(ObservationStoreOpenStatus.Ready, new SqliteObservationReader(normalized), probe.Message, probe),
            ObservationDatabaseProbeStatus.Missing =>
                new ObservationReaderOpenResult(ObservationStoreOpenStatus.Missing, null, probe.Message, probe),
            ObservationDatabaseProbeStatus.UnsupportedDatabaseVersion =>
                new ObservationReaderOpenResult(ObservationStoreOpenStatus.UnsupportedDatabaseVersion, null, probe.Message, probe),
            ObservationDatabaseProbeStatus.NativeSqliteTooOld =>
                new ObservationReaderOpenResult(ObservationStoreOpenStatus.NativeSqliteTooOld, null, probe.Message, probe),
            ObservationDatabaseProbeStatus.CorruptDatabase =>
                new ObservationReaderOpenResult(ObservationStoreOpenStatus.CorruptDatabase, null, probe.Message, probe),
            _ => new ObservationReaderOpenResult(ObservationStoreOpenStatus.Unavailable, null, probe.Message, probe),
        };
    }

    public async ValueTask<ObservationReadResult> ReadCurrentAsync(
        ObservationScope scope,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(scope);
        try
        {
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = options.DatabasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Shared,
                DefaultTimeout = Math.Max(1, (int)Math.Ceiling(options.BusyTimeout.TotalSeconds)),
            };
            await using var connection = new SqliteConnection(builder.ToString());
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT revision, scope_json, capture_json, payload_contract, payload_version,
                       payload_json, is_stale, stale_reason, stale_observed_at_utc,
                       last_confirmed_at_utc, confirmation_count
                FROM current_projection
                WHERE scope_key = $scope_key;
                """;
            command.Parameters.AddWithValue("$scope_key", CreateScopeKey(scope));
            await using var row = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await row.ReadAsync(cancellationToken).ConfigureAwait(false))
                return new ObservationReadResult(ObservationReadStatus.NotObserved, null, "No trusted observation exists for this scope.");

            var observation = new TrustedObservation(
                row.GetInt64(0),
                JsonSerializer.Deserialize<ObservationScope>(row.GetString(1), JsonOptions)
                    ?? throw new InvalidDataException("Stored observation scope is null."),
                JsonSerializer.Deserialize<ObservationCapture>(row.GetString(2), JsonOptions)
                    ?? throw new InvalidDataException("Stored observation capture is null."),
                new ObservationPayload(row.GetString(3), row.GetInt32(4), row.GetString(5)),
                row.GetBoolean(6),
                row.IsDBNull(7) ? null : row.GetString(7),
                row.IsDBNull(8) ? null : ParseUtc(row.GetString(8)),
                ParseUtc(row.GetString(9)),
                row.GetInt32(10));
            return new ObservationReadResult(ObservationReadStatus.Found, observation, "The latest trusted observation was found.");
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is 5 or 6)
        {
            return new ObservationReadResult(ObservationReadStatus.Busy, null, "The observation database remained busy beyond the bounded wait.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException or JsonException)
        {
            return new ObservationReadResult(ObservationReadStatus.Unavailable, null, ex.Message);
        }
    }

    public async ValueTask<ObservationCollectionReadResult> ReadCurrentByOwnerAsync(
        ObservationOwner owner,
        ObservationContainerKind container,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(owner);
        try
        {
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = options.DatabasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Shared,
                DefaultTimeout = Math.Max(1, (int)Math.Ceiling(options.BusyTimeout.TotalSeconds)),
            };
            await using var connection = new SqliteConnection(builder.ToString());
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT revision, scope_json, capture_json, payload_contract, payload_version,
                       payload_json, is_stale, stale_reason, stale_observed_at_utc,
                       last_confirmed_at_utc, confirmation_count
                FROM current_projection
                WHERE owner_local_content_id = $owner_local_content_id
                  AND owner_home_world_id = $owner_home_world_id
                  AND container_kind = $container_kind
                ORDER BY scope_key;
                """;
            command.Parameters.AddWithValue("$owner_local_content_id", owner.LocalContentId.ToString("X16", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$owner_home_world_id", owner.HomeWorldId);
            command.Parameters.AddWithValue("$container_kind", (int)container);
            await using var row = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var observations = new List<TrustedObservation>();
            while (await row.ReadAsync(cancellationToken).ConfigureAwait(false))
                observations.Add(ReadObservation(row));
            return new ObservationCollectionReadResult(
                ObservationReadStatus.Found,
                observations,
                $"Found {observations.Count} trusted observation(s) for the owner and container.");
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is 5 or 6)
        {
            return new ObservationCollectionReadResult(ObservationReadStatus.Busy, [], "The observation database remained busy beyond the bounded wait.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException or JsonException)
        {
            return new ObservationCollectionReadResult(ObservationReadStatus.Unavailable, [], ex.Message);
        }
    }

    public ValueTask DisposeAsync()
    {
        disposed = true;
        return ValueTask.CompletedTask;
    }

    private static string CreateScopeKey(ObservationScope scope) => string.Create(
        CultureInfo.InvariantCulture,
        $"{scope.Owner.LocalContentId:X16}:{scope.Owner.HomeWorldId}:{(int)scope.Subject.Kind}:{scope.Subject.Id:X16}:{(int)scope.Container}");

    private static DateTimeOffset ParseUtc(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();

    private static TrustedObservation ReadObservation(SqliteDataReader row) => new(
        row.GetInt64(0),
        JsonSerializer.Deserialize<ObservationScope>(row.GetString(1), JsonOptions)
            ?? throw new InvalidDataException("Stored observation scope is null."),
        JsonSerializer.Deserialize<ObservationCapture>(row.GetString(2), JsonOptions)
            ?? throw new InvalidDataException("Stored observation capture is null."),
        new ObservationPayload(row.GetString(3), row.GetInt32(4), row.GetString(5)),
        row.GetBoolean(6),
        row.IsDBNull(7) ? null : row.GetString(7),
        row.IsDBNull(8) ? null : ParseUtc(row.GetString(8)),
        ParseUtc(row.GetString(9)),
        row.GetInt32(10));
}
