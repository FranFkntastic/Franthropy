using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Franthropy.Observations.V1;
using Microsoft.Data.Sqlite;

namespace Franthropy.Observations.Storage;

public sealed class SqliteObservationStore : IObservationStore
{
    private const int SchemaUserVersion = 1000;
    private const int SqliteBusy = 5;
    private const int SqliteLocked = 6;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ObservationStoreOptions options;
    private bool disposed;

    private SqliteObservationStore(ObservationStoreOptions options)
    {
        this.options = options;
    }

    public event EventHandler<ObservationChange>? Changed;
    public string? LastMaintenanceError { get; private set; }
    public string? LastNotificationError { get; private set; }

    public static async ValueTask<ObservationStoreOpenResult> OpenAsync(
        ObservationStoreOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.DatabasePath);

        var databasePath = Path.GetFullPath(options.DatabasePath);
        var directory = Path.GetDirectoryName(databasePath);
        if (string.IsNullOrWhiteSpace(directory))
            return new ObservationStoreOpenResult(ObservationStoreOpenStatus.Unavailable, null, "The observation database directory is invalid.");

        try
        {
            Directory.CreateDirectory(directory);
            SQLitePCL.Batteries_V2.Init();
            await using var connection = CreateConnection(options, readOnly: false, pooling: false);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            var nativeVersion = await ReadNativeVersionAsync(connection, cancellationToken).ConfigureAwait(false);
            if (nativeVersion < options.MinimumNativeSqliteVersion)
            {
                return new ObservationStoreOpenResult(
                    ObservationStoreOpenStatus.NativeSqliteTooOld,
                    null,
                    $"Loaded SQLite {nativeVersion} is older than required {options.MinimumNativeSqliteVersion}.",
                    nativeVersion);
            }

            var integrity = await ExecuteScalarStringAsync(connection, "PRAGMA quick_check;", cancellationToken).ConfigureAwait(false);
            if (!string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase))
            {
                await connection.CloseAsync().ConfigureAwait(false);
                QuarantineDatabase(databasePath);
                return new ObservationStoreOpenResult(
                    ObservationStoreOpenStatus.CorruptDatabase,
                    null,
                    $"SQLite quick_check failed: {integrity}",
                    nativeVersion);
            }

            var userVersion = await ExecuteScalarInt64Async(connection, "PRAGMA user_version;", cancellationToken).ConfigureAwait(false);
            if (userVersion != 0 && userVersion != SchemaUserVersion)
            {
                return new ObservationStoreOpenResult(
                    ObservationStoreOpenStatus.UnsupportedDatabaseVersion,
                    null,
                    $"Database schema user_version {userVersion} is unsupported; expected {SchemaUserVersion}.",
                    nativeVersion);
            }

            if (userVersion == 0)
                await CreateSchemaAsync(connection, cancellationToken).ConfigureAwait(false);

            return new ObservationStoreOpenResult(
                ObservationStoreOpenStatus.Ready,
                new SqliteObservationStore(options with { DatabasePath = databasePath }),
                $"Observation database is ready with SQLite {nativeVersion}.",
                nativeVersion);
        }
        catch (SqliteException ex) when (IsBusy(ex))
        {
            return new ObservationStoreOpenResult(ObservationStoreOpenStatus.Unavailable, null, "The observation database is busy.");
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is 11 or 26)
        {
            try
            {
                QuarantineDatabase(databasePath);
                return new ObservationStoreOpenResult(ObservationStoreOpenStatus.CorruptDatabase, null, ex.Message);
            }
            catch (Exception quarantineException) when (quarantineException is IOException or UnauthorizedAccessException)
            {
                return new ObservationStoreOpenResult(
                    ObservationStoreOpenStatus.Unavailable,
                    null,
                    $"The observation database is corrupt and could not be quarantined: {quarantineException.Message}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        {
            return new ObservationStoreOpenResult(ObservationStoreOpenStatus.Unavailable, null, ex.Message);
        }
    }

    public async ValueTask<ObservationWriteResult> WriteAsync(
        ObservationEnvelope observation,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(observation);
        var validation = ObservationValidator.Validate(observation);
        var scopeKey = CreateScopeKey(observation.Scope);
        ObservationChange? change = null;

        try
        {
            await using var connection = CreateConnection(options, readOnly: false);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var current = await ReadCurrentRowAsync(connection, transaction, scopeKey, cancellationToken).ConfigureAwait(false);

            ObservationWriteResult result;
            var sourceOrder = current is null ? 1 : CompareSourceOrder(observation.Capture, current);
            if (current is not null && sourceOrder < 0)
            {
                result = new ObservationWriteResult(
                    ObservationWriteStatus.IgnoredOlderRevision,
                    "An older revision cannot change trusted state.",
                    current.Revision);
            }
            else if (current is not null && sourceOrder == 0)
            {
                result = new ObservationWriteResult(
                    ObservationWriteStatus.IgnoredRepeatedRevision,
                    "A repeated revision cannot change trusted state.",
                    current.Revision);
            }
            else if (validation.Status != ObservationValidationStatus.Authoritative)
            {
                await InsertHistoryAsync(connection, transaction, observation, validation, null, null, null, cancellationToken).ConfigureAwait(false);
                if (current is not null && validation.Status is ObservationValidationStatus.Unavailable or ObservationValidationStatus.Partial)
                {
                    var storeRevision = await NextRevisionAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
                    await MarkStaleAsync(
                        connection,
                        transaction,
                        scopeKey,
                        storeRevision,
                        validation.Message,
                        observation.Capture.ObservedAtUtc,
                        cancellationToken).ConfigureAwait(false);
                    result = new ObservationWriteResult(
                        ObservationWriteStatus.PreservedAsStale,
                        validation.Message,
                        storeRevision);
                    change = new ObservationChange(
                        observation.Scope,
                        storeRevision,
                        ObservationChangeKind.MarkedStale,
                        observation.Capture.ObservedAtUtc);
                }
                else
                {
                    result = new ObservationWriteResult(
                        ObservationWriteStatus.Rejected,
                        validation.Message,
                        current?.Revision);
                }
            }
            else
            {
                var payload = observation.Payload!;
                var payloadHash = Hash(payload.Json);
                if (current is not null && string.Equals(current.PayloadSha256, payloadHash, StringComparison.Ordinal))
                {
                    var storeRevision = await NextRevisionAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
                    await InsertHistoryAsync(connection, transaction, observation, validation, storeRevision, null, payloadHash, cancellationToken).ConfigureAwait(false);
                    await ConfirmCurrentAsync(connection, transaction, scopeKey, storeRevision, observation.Capture, cancellationToken).ConfigureAwait(false);
                    result = new ObservationWriteResult(
                        ObservationWriteStatus.AcceptedConfirmed,
                        "The newer revision confirms the existing trusted payload.",
                        storeRevision);
                    change = new ObservationChange(
                        observation.Scope,
                        storeRevision,
                        ObservationChangeKind.Confirmed,
                        observation.Capture.ObservedAtUtc);
                }
                else
                {
                    var storeRevision = await NextRevisionAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
                    await InsertHistoryAsync(connection, transaction, observation, validation, storeRevision, payload.Json, payloadHash, cancellationToken).ConfigureAwait(false);
                    await ReplaceCurrentAsync(connection, transaction, observation, storeRevision, payloadHash, cancellationToken).ConfigureAwait(false);
                    result = new ObservationWriteResult(
                        ObservationWriteStatus.AcceptedChanged,
                        current is null ? "The first trusted observation was accepted." : "The trusted observation was replaced atomically.",
                        storeRevision);
                    change = new ObservationChange(
                        observation.Scope,
                        storeRevision,
                        ObservationChangeKind.Replaced,
                        observation.Capture.ObservedAtUtc);
                }
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            if (change is not null)
                PublishChange(change);
            if (result.Status is ObservationWriteStatus.AcceptedChanged or ObservationWriteStatus.AcceptedConfirmed)
            {
                try
                {
                    await RunDueCleanupAsync(connection, observation.Capture.ObservedAtUtc, cancellationToken).ConfigureAwait(false);
                    LastMaintenanceError = null;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
                {
                    LastMaintenanceError = ex.Message;
                }
            }
            return result;
        }
        catch (SqliteException ex) when (IsBusy(ex))
        {
            return new ObservationWriteResult(ObservationWriteStatus.Busy, "The observation database remained busy beyond the bounded wait.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        {
            return new ObservationWriteResult(ObservationWriteStatus.Unavailable, ex.Message);
        }
    }

    public async ValueTask<ObservationReadResult> ReadCurrentAsync(
        ObservationScope scope,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(scope);

        try
        {
            await using var connection = CreateConnection(options, readOnly: true);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            var row = await ReadCurrentRowAsync(connection, null, CreateScopeKey(scope), cancellationToken).ConfigureAwait(false);
            if (row is null)
                return new ObservationReadResult(ObservationReadStatus.NotObserved, null, "No trusted observation exists for this scope.");

            var observation = new TrustedObservation(
                row.Revision,
                JsonSerializer.Deserialize<ObservationScope>(row.ScopeJson, JsonOptions)
                    ?? throw new InvalidDataException("Stored observation scope is null."),
                JsonSerializer.Deserialize<ObservationCapture>(row.CaptureJson, JsonOptions)
                    ?? throw new InvalidDataException("Stored observation capture is null."),
                new ObservationPayload(row.PayloadContract, row.PayloadVersion, row.PayloadJson),
                row.IsStale,
                row.StaleReason,
                ParseNullableUtc(row.StaleObservedAtUtc),
                ParseUtc(row.LastConfirmedAtUtc),
                row.ConfirmationCount);
            return new ObservationReadResult(ObservationReadStatus.Found, observation, "The latest trusted observation was found.");
        }
        catch (SqliteException ex) when (IsBusy(ex))
        {
            return new ObservationReadResult(ObservationReadStatus.Busy, null, "The observation database remained busy beyond the bounded wait.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException or JsonException)
        {
            return new ObservationReadResult(ObservationReadStatus.Unavailable, null, ex.Message);
        }
    }

    public async ValueTask<ObservationWriteResult> InvalidateAsync(
        ObservationScope scope,
        string reason,
        DateTimeOffset invalidatedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (invalidatedAtUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("Invalidation timestamps must be UTC.", nameof(invalidatedAtUtc));

        try
        {
            await using var connection = CreateConnection(options, readOnly: false);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var key = CreateScopeKey(scope);
            var current = await ReadCurrentRowAsync(connection, transaction, key, cancellationToken).ConfigureAwait(false);
            if (current is null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new ObservationWriteResult(ObservationWriteStatus.Rejected, "No trusted observation exists to invalidate.");
            }

            var storeRevision = await NextRevisionAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            await MarkStaleAsync(connection, transaction, key, storeRevision, reason, invalidatedAtUtc, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            PublishChange(new ObservationChange(scope, storeRevision, ObservationChangeKind.Invalidated, invalidatedAtUtc));
            return new ObservationWriteResult(ObservationWriteStatus.PreservedAsStale, reason, storeRevision);
        }
        catch (SqliteException ex) when (IsBusy(ex))
        {
            return new ObservationWriteResult(ObservationWriteStatus.Busy, "The observation database remained busy beyond the bounded wait.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        {
            return new ObservationWriteResult(ObservationWriteStatus.Unavailable, ex.Message);
        }
    }

    public ValueTask DisposeAsync()
    {
        disposed = true;
        return ValueTask.CompletedTask;
    }

    internal async ValueTask<int> CountHistoryPayloadsAsync(ObservationScope scope, CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection(options, readOnly: true);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM observation_history WHERE scope_key = $scope AND payload_json IS NOT NULL;";
        command.Parameters.AddWithValue("$scope", CreateScopeKey(scope));
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    private static SqliteConnection CreateConnection(ObservationStoreOptions options, bool readOnly, bool pooling = true)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = options.DatabasePath,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = pooling,
            DefaultTimeout = Math.Max(1, (int)Math.Ceiling(options.BusyTimeout.TotalSeconds)),
        };
        return new SqliteConnection(builder.ToString());
    }

    private static async ValueTask CreateSchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using (var mode = connection.CreateCommand())
        {
            mode.CommandText = "PRAGMA journal_mode = WAL; PRAGMA synchronous = FULL;";
            await mode.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            CREATE TABLE observation_metadata (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            INSERT INTO observation_metadata(key, value) VALUES
                ('schema_major', '1'),
                ('schema_minor', '0'),
                ('contract_major', '1'),
                ('contract_minor', '0'),
                ('minimum_writer_capability', '1'),
                ('next_revision', '0');
            CREATE TABLE observation_history (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                scope_key TEXT NOT NULL,
                source_revision INTEGER NOT NULL,
                store_revision INTEGER NULL,
                observed_at_utc TEXT NOT NULL,
                status INTEGER NOT NULL,
                validation_code INTEGER NOT NULL,
                message TEXT NOT NULL,
                scope_json TEXT NOT NULL,
                capture_json TEXT NOT NULL,
                payload_contract TEXT NULL,
                payload_version INTEGER NULL,
                payload_json TEXT NULL,
                payload_sha256 TEXT NULL
            );
            CREATE INDEX ix_observation_history_scope_revision
                ON observation_history(scope_key, store_revision DESC, source_revision DESC);
            CREATE INDEX ix_observation_history_observed
                ON observation_history(observed_at_utc);
            CREATE TABLE current_projection (
                scope_key TEXT PRIMARY KEY,
                revision INTEGER NOT NULL,
                observed_at_utc TEXT NOT NULL,
                scope_json TEXT NOT NULL,
                capture_json TEXT NOT NULL,
                payload_contract TEXT NOT NULL,
                payload_version INTEGER NOT NULL,
                payload_json TEXT NOT NULL,
                payload_sha256 TEXT NOT NULL,
                is_stale INTEGER NOT NULL,
                stale_reason TEXT NULL,
                stale_observed_at_utc TEXT NULL,
                last_confirmed_at_utc TEXT NOT NULL,
                confirmation_count INTEGER NOT NULL
            );
            PRAGMA user_version = 1000;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask InsertHistoryAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        ObservationEnvelope observation,
        ObservationValidationResult validation,
        long? storeRevision,
        string? payloadJson,
        string? payloadHash,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            INSERT INTO observation_history(
                scope_key, source_revision, store_revision, observed_at_utc, status, validation_code, message,
                scope_json, capture_json, payload_contract, payload_version, payload_json, payload_sha256)
            VALUES(
                $scope_key, $source_revision, $store_revision, $observed_at, $status, $code, $message,
                $scope_json, $capture_json, $payload_contract, $payload_version, $payload_json, $payload_sha256);
            """;
        command.Parameters.AddWithValue("$scope_key", CreateScopeKey(observation.Scope));
        command.Parameters.AddWithValue("$source_revision", observation.Capture.SourceRevision);
        command.Parameters.AddWithValue("$store_revision", (object?)storeRevision ?? DBNull.Value);
        command.Parameters.AddWithValue("$observed_at", FormatUtc(observation.Capture.ObservedAtUtc));
        command.Parameters.AddWithValue("$status", (int)validation.Status);
        command.Parameters.AddWithValue("$code", (int)validation.Code);
        command.Parameters.AddWithValue("$message", validation.Message);
        command.Parameters.AddWithValue("$scope_json", JsonSerializer.Serialize(observation.Scope, JsonOptions));
        command.Parameters.AddWithValue("$capture_json", JsonSerializer.Serialize(observation.Capture, JsonOptions));
        command.Parameters.AddWithValue("$payload_contract", (object?)observation.Payload?.Contract ?? DBNull.Value);
        command.Parameters.AddWithValue("$payload_version", (object?)observation.Payload?.Version ?? DBNull.Value);
        command.Parameters.AddWithValue("$payload_json", (object?)payloadJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$payload_sha256", (object?)payloadHash ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask ReplaceCurrentAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        ObservationEnvelope observation,
        long storeRevision,
        string payloadHash,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            INSERT INTO current_projection(
                scope_key, revision, observed_at_utc, scope_json, capture_json,
                payload_contract, payload_version, payload_json, payload_sha256,
                is_stale, stale_reason, stale_observed_at_utc, last_confirmed_at_utc, confirmation_count)
            VALUES(
                $scope_key, $revision, $observed_at, $scope_json, $capture_json,
                $payload_contract, $payload_version, $payload_json, $payload_sha256,
                0, NULL, NULL, $observed_at, 1)
            ON CONFLICT(scope_key) DO UPDATE SET
                revision = excluded.revision,
                observed_at_utc = excluded.observed_at_utc,
                scope_json = excluded.scope_json,
                capture_json = excluded.capture_json,
                payload_contract = excluded.payload_contract,
                payload_version = excluded.payload_version,
                payload_json = excluded.payload_json,
                payload_sha256 = excluded.payload_sha256,
                is_stale = 0,
                stale_reason = NULL,
                stale_observed_at_utc = NULL,
                last_confirmed_at_utc = excluded.last_confirmed_at_utc,
                confirmation_count = 1;
            """;
        command.Parameters.AddWithValue("$scope_key", CreateScopeKey(observation.Scope));
        command.Parameters.AddWithValue("$revision", storeRevision);
        command.Parameters.AddWithValue("$observed_at", FormatUtc(observation.Capture.ObservedAtUtc));
        command.Parameters.AddWithValue("$scope_json", JsonSerializer.Serialize(observation.Scope, JsonOptions));
        command.Parameters.AddWithValue("$capture_json", JsonSerializer.Serialize(observation.Capture, JsonOptions));
        command.Parameters.AddWithValue("$payload_contract", observation.Payload!.Contract);
        command.Parameters.AddWithValue("$payload_version", observation.Payload.Version);
        command.Parameters.AddWithValue("$payload_json", observation.Payload.Json);
        command.Parameters.AddWithValue("$payload_sha256", payloadHash);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask ConfirmCurrentAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        string scopeKey,
        long storeRevision,
        ObservationCapture capture,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            UPDATE current_projection SET
                revision = $revision,
                observed_at_utc = $observed_at,
                capture_json = $capture_json,
                is_stale = 0,
                stale_reason = NULL,
                stale_observed_at_utc = NULL,
                last_confirmed_at_utc = $observed_at,
                confirmation_count = confirmation_count + 1
            WHERE scope_key = $scope_key;
            """;
        command.Parameters.AddWithValue("$revision", storeRevision);
        command.Parameters.AddWithValue("$observed_at", FormatUtc(capture.ObservedAtUtc));
        command.Parameters.AddWithValue("$capture_json", JsonSerializer.Serialize(capture, JsonOptions));
        command.Parameters.AddWithValue("$scope_key", scopeKey);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask MarkStaleAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        string scopeKey,
        long storeRevision,
        string reason,
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            UPDATE current_projection SET
                revision = $revision,
                is_stale = 1,
                stale_reason = $reason,
                stale_observed_at_utc = $observed_at
            WHERE scope_key = $scope_key;
            """;
        command.Parameters.AddWithValue("$reason", reason);
        command.Parameters.AddWithValue("$revision", storeRevision);
        command.Parameters.AddWithValue("$observed_at", FormatUtc(observedAtUtc));
        command.Parameters.AddWithValue("$scope_key", scopeKey);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<CurrentRow?> ReadCurrentRowAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction? transaction,
        string scopeKey,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction?)transaction;
        command.CommandText = """
            SELECT revision, scope_json, capture_json, payload_contract, payload_version,
                   payload_json, payload_sha256, is_stale, stale_reason,
                   stale_observed_at_utc, last_confirmed_at_utc, confirmation_count
            FROM current_projection
            WHERE scope_key = $scope_key;
            """;
        command.Parameters.AddWithValue("$scope_key", scopeKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;
        return new CurrentRow(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetInt32(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetBoolean(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.GetString(10),
            reader.GetInt32(11));
    }

    private static int CompareSourceOrder(ObservationCapture incoming, CurrentRow current)
    {
        var existing = JsonSerializer.Deserialize<ObservationCapture>(current.CaptureJson, JsonOptions)
            ?? throw new InvalidDataException("Stored observation capture is null.");
        if (string.Equals(
                incoming.Provenance.PluginInstanceId,
                existing.Provenance.PluginInstanceId,
                StringComparison.Ordinal))
        {
            return incoming.SourceRevision.CompareTo(existing.SourceRevision);
        }

        return incoming.ObservedAtUtc.CompareTo(existing.ObservedAtUtc);
    }

    private static async ValueTask<long> NextRevisionAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            UPDATE observation_metadata
            SET value = CAST(value AS INTEGER) + 1
            WHERE key = 'next_revision'
            RETURNING CAST(value AS INTEGER);
            """;
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    private async ValueTask RunDueCleanupAsync(
        SqliteConnection connection,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var lastCleanupText = await ExecuteScalarStringAsync(
            connection,
            "SELECT value FROM observation_metadata WHERE key = 'last_cleanup_at_utc';",
            cancellationToken).ConfigureAwait(false);
        if (DateTimeOffset.TryParse(lastCleanupText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var lastCleanup) &&
            nowUtc - lastCleanup < TimeSpan.FromHours(1))
            return;

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            DELETE FROM observation_history
            WHERE payload_json IS NOT NULL
              AND observed_at_utc < $payload_cutoff;
            DELETE FROM observation_history
            WHERE payload_json IS NULL
              AND observed_at_utc < $metadata_cutoff;
            DELETE FROM observation_history
            WHERE payload_json IS NOT NULL
              AND id IN (
                  SELECT id FROM (
                      SELECT id,
                             ROW_NUMBER() OVER (PARTITION BY scope_key ORDER BY store_revision DESC, source_revision DESC) AS position
                      FROM observation_history
                      WHERE payload_json IS NOT NULL
                  )
                  WHERE position > 64
              );
            DELETE FROM observation_history
            WHERE payload_json IS NULL
              AND id IN (
                  SELECT id FROM observation_history
                  WHERE payload_json IS NULL
                  ORDER BY id DESC
                  LIMIT -1 OFFSET 256
              );
            INSERT INTO observation_metadata(key, value)
            VALUES('last_cleanup_at_utc', $now)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        command.Parameters.AddWithValue("$payload_cutoff", FormatUtc(nowUtc - TimeSpan.FromDays(30)));
        command.Parameters.AddWithValue("$metadata_cutoff", FormatUtc(nowUtc - TimeSpan.FromDays(14)));
        command.Parameters.AddWithValue("$now", FormatUtc(nowUtc));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        if (await ReadHistorySizeAsync(connection, cancellationToken).ConfigureAwait(false) > options.HistorySoftLimitBytes)
            await PruneToSoftLimitAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask PruneToSoftLimitAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        while (await ReadHistorySizeAsync(connection, cancellationToken).ConfigureAwait(false) > options.HistorySoftLimitBytes)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                DELETE FROM observation_history
                WHERE id IN (SELECT id FROM observation_history ORDER BY id LIMIT 128);
                """;
            var deleted = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (deleted == 0)
                break;
        }
    }

    private static async ValueTask<long> ReadHistorySizeAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COALESCE(SUM(
                length(scope_json) + length(capture_json) + length(message) +
                COALESCE(length(payload_contract), 0) + COALESCE(length(payload_json), 0) +
                COALESCE(length(payload_sha256), 0)), 0)
            FROM observation_history;
            """;
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    private void PublishChange(ObservationChange change)
    {
        var subscribers = Changed;
        if (subscribers is null)
            return;
        foreach (var subscriber in subscribers.GetInvocationList().Cast<EventHandler<ObservationChange>>())
        {
            try
            {
                subscriber(this, change);
            }
            catch (Exception ex)
            {
                LastNotificationError = ex.Message;
            }
        }
    }

    private static string CreateScopeKey(ObservationScope scope) => string.Create(
        CultureInfo.InvariantCulture,
        $"{scope.Owner.LocalContentId:X16}:{scope.Owner.HomeWorldId}:{(int)scope.Subject.Kind}:{scope.Subject.Id:X16}:{(int)scope.Container}");

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string FormatUtc(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset ParseUtc(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();
    private static DateTimeOffset? ParseNullableUtc(string? value) => value is null ? null : ParseUtc(value);
    private static bool IsBusy(SqliteException ex) => ex.SqliteErrorCode is SqliteBusy or SqliteLocked;

    private static async ValueTask<Version> ReadNativeVersionAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var value = await ExecuteScalarStringAsync(connection, "SELECT sqlite_version();", cancellationToken).ConfigureAwait(false);
        return Version.TryParse(value, out var version) ? version : new Version(0, 0);
    }

    private static async ValueTask<string?> ExecuteScalarStringAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))?.ToString();
    }

    private static async ValueTask<long> ExecuteScalarInt64Async(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    private static void QuarantineDatabase(string databasePath)
    {
        var directory = Path.GetDirectoryName(databasePath)!;
        var quarantine = Path.Combine(directory, "quarantine");
        Directory.CreateDirectory(quarantine);
        var suffix = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);
        foreach (var source in new[] { databasePath, databasePath + "-wal", databasePath + "-shm" })
        {
            if (!File.Exists(source))
                continue;
            var target = Path.Combine(quarantine, $"{Path.GetFileName(source)}.{suffix}");
            File.Move(source, target, overwrite: false);
        }
    }

    private sealed record CurrentRow(
        long Revision,
        string ScopeJson,
        string CaptureJson,
        string PayloadContract,
        int PayloadVersion,
        string PayloadJson,
        string PayloadSha256,
        bool IsStale,
        string? StaleReason,
        string? StaleObservedAtUtc,
        string LastConfirmedAtUtc,
        int ConfirmationCount);
}
