using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Franthropy.Observations.V1;
using Microsoft.Data.Sqlite;

namespace Franthropy.Observations.Storage;

public sealed class SqliteObservationStore : IObservationStore
{
    private const int SchemaUserVersion = 1002;
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
            if (userVersion is not 0 and not 1000 and not 1001 and not SchemaUserVersion)
            {
                return new ObservationStoreOpenResult(
                    ObservationStoreOpenStatus.UnsupportedDatabaseVersion,
                    null,
                    $"Database schema user_version {userVersion} is unsupported; expected {SchemaUserVersion}.",
                    nativeVersion);
            }

            if (userVersion != 0)
            {
                var minimumWriterCapability = await ReadMetadataIntAsync(
                    connection,
                    "minimum_writer_capability",
                    cancellationToken).ConfigureAwait(false);
                if (options.WriterCapability < minimumWriterCapability)
                {
                    return new ObservationStoreOpenResult(
                        ObservationStoreOpenStatus.IncompatibleWriterCapability,
                        null,
                        $"Writer capability {options.WriterCapability} is below the database minimum {minimumWriterCapability}.",
                        nativeVersion);
                }
            }

            if (userVersion is 1000 or 1001 && options.WriterCapability < 2)
            {
                return new ObservationStoreOpenResult(
                    ObservationStoreOpenStatus.IncompatibleWriterCapability,
                    null,
                    $"Writer capability {options.WriterCapability} cannot migrate the database to schema 1.2, which requires capability 2.",
                    nativeVersion);
            }

            if (userVersion == 0 && options.WriterCapability < 2)
            {
                return new ObservationStoreOpenResult(
                    ObservationStoreOpenStatus.IncompatibleWriterCapability,
                    null,
                    $"Writer capability {options.WriterCapability} is below the database minimum 2.",
                    nativeVersion);
            }

            if (userVersion == 0)
                await CreateSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
            else
            {
                if (userVersion == 1000)
                {
                    await MigrateFrom1000Async(connection, options, cancellationToken).ConfigureAwait(false);
                    userVersion = 1001;
                }
                if (userVersion == 1001)
                    await MigrateFrom1001Async(connection, options, cancellationToken).ConfigureAwait(false);
            }

            var schemaMajor = await ReadMetadataIntAsync(connection, "schema_major", cancellationToken).ConfigureAwait(false);
            var schemaMinor = await ReadMetadataIntAsync(connection, "schema_minor", cancellationToken).ConfigureAwait(false);
            var contractMajor = await ReadMetadataIntAsync(connection, "contract_major", cancellationToken).ConfigureAwait(false);
            if (schemaMajor != ObservationContract.SchemaVersion.Major ||
                schemaMinor != ObservationContract.SchemaVersion.Minor ||
                contractMajor != ObservationContract.Version.Major)
            {
                return new ObservationStoreOpenResult(
                    ObservationStoreOpenStatus.UnsupportedDatabaseVersion,
                    null,
                    $"Database metadata schema {schemaMajor}.{schemaMinor} and contract major {contractMajor} are unsupported.",
                    nativeVersion);
            }

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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException or InvalidOperationException)
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
                        observation.Capture,
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
                if (IsInventoryContainer(observation.Scope.Container))
                {
                    var storeRevision = await NextRevisionAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
                    var inventory = payload.Deserialize<InventoryObservationPayload>(payload.Contract, payload.Version);
                    await InsertHistoryAsync(connection, transaction, observation, validation, storeRevision, payload.Json, payloadHash, cancellationToken).ConfigureAwait(false);
                    await ReplaceCurrentAsync(connection, transaction, observation, storeRevision, payloadHash, cancellationToken).ConfigureAwait(false);
                    await ReplaceInventoryBaselineAsync(connection, transaction, scopeKey, storeRevision, inventory, cancellationToken).ConfigureAwait(false);
                    result = new ObservationWriteResult(
                        ObservationWriteStatus.AcceptedChanged,
                        current is null
                            ? "The first trusted inventory baseline was accepted."
                            : "The trusted inventory baseline was reconciled atomically.",
                        storeRevision);
                    change = new ObservationChange(
                        observation.Scope,
                        storeRevision,
                        ObservationChangeKind.Replaced,
                        observation.Capture.ObservedAtUtc);
                }
                else if (current is not null && string.Equals(current.PayloadSha256, payloadHash, StringComparison.Ordinal))
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

            var observation = await ToTrustedObservationAsync(connection, row, cancellationToken).ConfigureAwait(false);
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

    public async ValueTask<ObservationCollectionReadResult> ReadCurrentByOwnerAsync(
        ObservationOwner owner,
        ObservationContainerKind container,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(owner);
        try
        {
            await using var connection = CreateConnection(options, readOnly: true);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            var rows = await ReadCurrentRowsByOwnerAsync(connection, owner, container, cancellationToken).ConfigureAwait(false);
            var observations = new List<TrustedObservation>(rows.Count);
            foreach (var row in rows)
                observations.Add(await ToTrustedObservationAsync(connection, row, cancellationToken).ConfigureAwait(false));
            return new ObservationCollectionReadResult(
                ObservationReadStatus.Found,
                observations,
                $"Found {rows.Count} trusted observation(s) for the owner and container.");
        }
        catch (SqliteException ex) when (IsBusy(ex))
        {
            return new ObservationCollectionReadResult(ObservationReadStatus.Busy, [], "The observation database remained busy beyond the bounded wait.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException or JsonException)
        {
            return new ObservationCollectionReadResult(ObservationReadStatus.Unavailable, [], ex.Message);
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
            await MarkStaleAsync(connection, transaction, key, storeRevision, reason, null, invalidatedAtUtc, cancellationToken).ConfigureAwait(false);
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
            Cache = readOnly ? SqliteCacheMode.Private : SqliteCacheMode.Shared,
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
                ('schema_minor', '2'),
                ('contract_major', '1'),
                ('contract_minor', '0'),
                ('minimum_writer_capability', '2'),
                ('next_revision', '0'),
                ('change_revision', '0');
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
                owner_local_content_id TEXT NOT NULL,
                owner_home_world_id INTEGER NOT NULL,
                container_kind INTEGER NOT NULL,
                revision INTEGER NOT NULL,
                observed_at_utc TEXT NOT NULL,
                scope_json TEXT NOT NULL,
                capture_json TEXT NOT NULL,
                source_order_capture_json TEXT NOT NULL,
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
            CREATE INDEX ix_current_projection_owner_container
                ON current_projection(owner_local_content_id, owner_home_world_id, container_kind);
            CREATE TABLE inventory_scope_projection (
                scope_key TEXT PRIMARY KEY,
                base_revision INTEGER NOT NULL,
                requested_container_ids_json TEXT NOT NULL,
                observed_container_ids_json TEXT NOT NULL
            );
            CREATE TABLE inventory_slot_projection (
                scope_key TEXT NOT NULL,
                container_id INTEGER NOT NULL,
                slot_index INTEGER NOT NULL,
                item_id INTEGER NOT NULL,
                quantity INTEGER NOT NULL,
                is_high_quality INTEGER NOT NULL,
                PRIMARY KEY(scope_key, container_id, slot_index)
            );
            CREATE INDEX ix_inventory_slot_projection_scope_item
                ON inventory_slot_projection(scope_key, item_id, is_high_quality);
            CREATE TABLE inventory_slot_change (
                store_revision INTEGER NOT NULL,
                scope_key TEXT NOT NULL,
                capture_json TEXT NOT NULL,
                container_id INTEGER NOT NULL,
                slot_index INTEGER NOT NULL,
                previous_item_id INTEGER NULL,
                previous_quantity INTEGER NULL,
                previous_is_high_quality INTEGER NULL,
                current_item_id INTEGER NULL,
                current_quantity INTEGER NULL,
                current_is_high_quality INTEGER NULL,
                PRIMARY KEY(store_revision, scope_key, container_id, slot_index)
            );
            CREATE INDEX ix_inventory_slot_change_scope_revision
                ON inventory_slot_change(scope_key, store_revision);
            PRAGMA user_version = 1002;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask MigrateFrom1000Async(
        SqliteConnection connection,
        ObservationStoreOptions options,
        CancellationToken cancellationToken)
    {
        var databasePath = Path.GetFullPath(options.DatabasePath);
        var directory = Path.GetDirectoryName(databasePath)!;
        var lockPath = Path.GetFullPath(options.MigrationLockPath ?? Path.Combine(directory, "migration.lock"));
        var backupDirectory = Path.GetFullPath(options.BackupDirectory ?? Path.Combine(directory, "backups"));
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        Directory.CreateDirectory(backupDirectory);

        await using var migrationLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
        try
        {
            migrationLock.Lock(0, 1);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException("Another compatible host is migrating the shared observation database.", ex);
        }

        try
        {
            var backupPath = Path.Combine(
                backupDirectory,
                $"observations-v1.0-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}.db");
            var backupBuilder = new SqliteConnectionStringBuilder
            {
                DataSource = backupPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false,
            };
            await using (var backup = new SqliteConnection(backupBuilder.ToString()))
            {
                await backup.OpenAsync(cancellationToken).ConfigureAwait(false);
                connection.BackupDatabase(backup);
            }

            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                ALTER TABLE current_projection ADD COLUMN owner_local_content_id TEXT NOT NULL DEFAULT '';
                ALTER TABLE current_projection ADD COLUMN owner_home_world_id INTEGER NOT NULL DEFAULT 0;
                ALTER TABLE current_projection ADD COLUMN container_kind INTEGER NOT NULL DEFAULT 0;
                ALTER TABLE current_projection ADD COLUMN source_order_capture_json TEXT NOT NULL DEFAULT '{}';
                UPDATE current_projection SET
                    owner_local_content_id = substr(scope_key, 1, 16),
                    owner_home_world_id = CAST(substr(scope_key, 18, instr(substr(scope_key, 18), ':') - 1) AS INTEGER),
                    container_kind = CAST(json_extract(scope_json, '$.container') AS INTEGER),
                    source_order_capture_json = capture_json;
                CREATE INDEX ix_current_projection_owner_container
                    ON current_projection(owner_local_content_id, owner_home_world_id, container_kind);
                INSERT INTO observation_metadata(key, value)
                VALUES('change_revision', COALESCE((SELECT value FROM observation_metadata WHERE key = 'next_revision'), '0'))
                ON CONFLICT(key) DO NOTHING;
                UPDATE observation_metadata SET value = '1' WHERE key = 'schema_minor';
                PRAGMA user_version = 1001;
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            options.BeforeMigrationCommit?.Invoke();
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            migrationLock.Unlock(0, 1);
        }
    }

    public async ValueTask<ObservationWriteResult> WriteInventoryDeltaAsync(
        InventoryObservationDelta observation,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(observation);
        var validation = ValidateInventoryDelta(observation, out var validationEnvelope);
        if (!validation.IsAuthoritative)
            return new ObservationWriteResult(ObservationWriteStatus.Rejected, validation.Message);

        var scopeKey = CreateScopeKey(observation.Scope);
        ObservationChange? notification = null;
        try
        {
            await using var connection = CreateConnection(options, readOnly: false);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var current = await ReadCurrentRowAsync(connection, transaction, scopeKey, cancellationToken).ConfigureAwait(false);
            var observedContainers = await ReadObservedInventoryContainersAsync(
                connection,
                transaction,
                scopeKey,
                cancellationToken).ConfigureAwait(false);
            if (current is null || current.IsStale || observedContainers is null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new ObservationWriteResult(
                    ObservationWriteStatus.Rejected,
                    "A fresh complete inventory baseline is required before slot changes can be applied.",
                    current?.Revision);
            }

            var unobservedContainer = observation.Updates
                .Select(update => update.ContainerId)
                .Cast<int?>()
                .FirstOrDefault(containerId => !observedContainers.Contains(containerId!.Value));
            if (unobservedContainer is { } missingContainer)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new ObservationWriteResult(
                    ObservationWriteStatus.Rejected,
                    $"Inventory container {missingContainer} was not observed by the current baseline; a fresh complete baseline is required.",
                    current.Revision);
            }

            var sourceOrder = CompareSourceOrder(observation.Capture, current);
            if (sourceOrder < 0)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new ObservationWriteResult(
                    ObservationWriteStatus.IgnoredOlderRevision,
                    "An older inventory revision cannot change trusted slots.",
                    current.Revision);
            }
            if (sourceOrder == 0)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new ObservationWriteResult(
                    ObservationWriteStatus.IgnoredRepeatedRevision,
                    "A repeated inventory revision cannot change trusted slots.",
                    current.Revision);
            }

            var changes = new List<InventorySlotChange>();
            foreach (var update in observation.Updates)
            {
                var previous = await ReadInventorySlotAsync(
                    connection,
                    transaction,
                    scopeKey,
                    update.ContainerId,
                    update.SlotIndex,
                    cancellationToken).ConfigureAwait(false);
                if (previous == update.Current)
                    continue;
                changes.Add(new InventorySlotChange(update.ContainerId, update.SlotIndex, previous, update.Current));
            }

            var storeRevision = await NextRevisionAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            if (changes.Count == 0)
            {
                await InsertHistoryAsync(
                    connection,
                    transaction,
                    validationEnvelope,
                    validation,
                    storeRevision,
                    null,
                    Hash(validationEnvelope.Payload!.Json),
                    cancellationToken).ConfigureAwait(false);
                await ConfirmCurrentAsync(connection, transaction, scopeKey, storeRevision, observation.Capture, cancellationToken).ConfigureAwait(false);
                notification = new ObservationChange(observation.Scope, storeRevision, ObservationChangeKind.Confirmed, observation.Capture.ObservedAtUtc);
            }
            else
            {
                await InsertInventoryDeltaHistoryAsync(connection, transaction, observation, validation, storeRevision, cancellationToken).ConfigureAwait(false);
                foreach (var change in changes)
                {
                    await ApplyInventorySlotChangeAsync(connection, transaction, scopeKey, storeRevision, observation.Capture, change, cancellationToken).ConfigureAwait(false);
                }
                await AdvanceInventoryCurrentAsync(connection, transaction, scopeKey, storeRevision, observation.Capture, cancellationToken).ConfigureAwait(false);
                notification = new ObservationChange(observation.Scope, storeRevision, ObservationChangeKind.Replaced, observation.Capture.ObservedAtUtc);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            PublishChange(notification);
            try
            {
                await RunDueCleanupAsync(connection, observation.Capture.ObservedAtUtc, cancellationToken).ConfigureAwait(false);
                LastMaintenanceError = null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
            {
                LastMaintenanceError = ex.Message;
            }
            return new ObservationWriteResult(
                changes.Count == 0 ? ObservationWriteStatus.AcceptedConfirmed : ObservationWriteStatus.AcceptedChanged,
                changes.Count == 0
                    ? "The newer inventory revision confirms the current slots."
                    : $"Applied {changes.Count} changed inventory slot(s) atomically.",
                storeRevision);
        }
        catch (SqliteException ex) when (IsBusy(ex))
        {
            return new ObservationWriteResult(ObservationWriteStatus.Busy, "The observation database remained busy beyond the bounded wait.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException or JsonException)
        {
            return new ObservationWriteResult(ObservationWriteStatus.Unavailable, ex.Message);
        }
    }

    public async ValueTask<InventoryChangeReadResult> ReadInventoryChangesAsync(
        ObservationOwner owner,
        long afterRevision,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentOutOfRangeException.ThrowIfNegative(afterRevision);
        try
        {
            await using var connection = CreateConnection(options, readOnly: true);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var currentRevision = await ReadMetadataLongAsync(connection, transaction, "next_revision", cancellationToken).ConfigureAwait(false);
            var baselines = await ReadInventoryBaselinesAsync(connection, transaction, owner, cancellationToken).ConfigureAwait(false);
            if (baselines.Count == 0)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new InventoryChangeReadResult(
                    InventoryChangeReadStatus.NotObserved,
                    currentRevision,
                    null,
                    [],
                    "No trusted inventory observation exists for this owner.");
            }

            var requiredBaseline = baselines.Where(revision => revision > afterRevision).DefaultIfEmpty().Max();
            if (requiredBaseline > 0)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new InventoryChangeReadResult(
                    InventoryChangeReadStatus.SnapshotRequired,
                    currentRevision,
                    requiredBaseline,
                    [],
                    $"Inventory baseline revision {requiredBaseline} is newer than consumer revision {afterRevision}; a current snapshot is required.");
            }

            var batches = await ReadInventoryChangeBatchesAsync(
                connection,
                transaction,
                owner,
                afterRevision,
                currentRevision,
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new InventoryChangeReadResult(
                batches.Count == 0 ? InventoryChangeReadStatus.NoChanges : InventoryChangeReadStatus.Found,
                currentRevision,
                null,
                batches,
                batches.Count == 0
                    ? "No inventory slot changes exist after the requested revision."
                    : $"Found {batches.Count} inventory change batch(es) after revision {afterRevision}.");
        }
        catch (SqliteException ex) when (IsBusy(ex))
        {
            return new InventoryChangeReadResult(InventoryChangeReadStatus.Busy, afterRevision, null, [], "The observation database remained busy beyond the bounded wait.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException or JsonException)
        {
            return new InventoryChangeReadResult(InventoryChangeReadStatus.Unavailable, afterRevision, null, [], ex.Message);
        }
    }

    private static async ValueTask MigrateFrom1001Async(
        SqliteConnection connection,
        ObservationStoreOptions options,
        CancellationToken cancellationToken)
    {
        var databasePath = Path.GetFullPath(options.DatabasePath);
        var directory = Path.GetDirectoryName(databasePath)!;
        var lockPath = Path.GetFullPath(options.MigrationLockPath ?? Path.Combine(directory, "migration.lock"));
        var backupDirectory = Path.GetFullPath(options.BackupDirectory ?? Path.Combine(directory, "backups"));
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        Directory.CreateDirectory(backupDirectory);

        await using var migrationLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
        try
        {
            migrationLock.Lock(0, 1);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException("Another compatible host is migrating the shared observation database.", ex);
        }

        try
        {
            var backupPath = Path.Combine(
                backupDirectory,
                $"observations-v1.1-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}.db");
            var backupBuilder = new SqliteConnectionStringBuilder
            {
                DataSource = backupPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false,
            };
            await using (var backup = new SqliteConnection(backupBuilder.ToString()))
            {
                await backup.OpenAsync(cancellationToken).ConfigureAwait(false);
                connection.BackupDatabase(backup);
            }

            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (var schema = connection.CreateCommand())
            {
                schema.Transaction = (SqliteTransaction)transaction;
                schema.CommandText = """
                    CREATE TABLE inventory_scope_projection (
                        scope_key TEXT PRIMARY KEY,
                        base_revision INTEGER NOT NULL,
                        requested_container_ids_json TEXT NOT NULL,
                        observed_container_ids_json TEXT NOT NULL
                    );
                    CREATE TABLE inventory_slot_projection (
                        scope_key TEXT NOT NULL,
                        container_id INTEGER NOT NULL,
                        slot_index INTEGER NOT NULL,
                        item_id INTEGER NOT NULL,
                        quantity INTEGER NOT NULL,
                        is_high_quality INTEGER NOT NULL,
                        PRIMARY KEY(scope_key, container_id, slot_index)
                    );
                    CREATE INDEX ix_inventory_slot_projection_scope_item
                        ON inventory_slot_projection(scope_key, item_id, is_high_quality);
                    CREATE TABLE inventory_slot_change (
                        store_revision INTEGER NOT NULL,
                        scope_key TEXT NOT NULL,
                        capture_json TEXT NOT NULL,
                        container_id INTEGER NOT NULL,
                        slot_index INTEGER NOT NULL,
                        previous_item_id INTEGER NULL,
                        previous_quantity INTEGER NULL,
                        previous_is_high_quality INTEGER NULL,
                        current_item_id INTEGER NULL,
                        current_quantity INTEGER NULL,
                        current_is_high_quality INTEGER NULL,
                        PRIMARY KEY(store_revision, scope_key, container_id, slot_index)
                    );
                    CREATE INDEX ix_inventory_slot_change_scope_revision
                        ON inventory_slot_change(scope_key, store_revision);
                    """;
                await schema.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            var baselines = new List<(string ScopeKey, long Revision, InventoryObservationPayload Payload)>();
            await using (var select = connection.CreateCommand())
            {
                select.Transaction = (SqliteTransaction)transaction;
                select.CommandText = """
                    SELECT scope_key, revision, payload_contract, payload_version, payload_json
                    FROM current_projection
                    WHERE container_kind IN ($player, $retainer, $saddlebag);
                    """;
                select.Parameters.AddWithValue("$player", (int)ObservationContainerKind.PlayerInventory);
                select.Parameters.AddWithValue("$retainer", (int)ObservationContainerKind.RetainerInventory);
                select.Parameters.AddWithValue("$saddlebag", (int)ObservationContainerKind.Saddlebag);
                await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var payload = new ObservationPayload(reader.GetString(2), reader.GetInt32(3), reader.GetString(4));
                    baselines.Add((reader.GetString(0), reader.GetInt64(1), payload.Deserialize<InventoryObservationPayload>(payload.Contract, payload.Version)));
                }
            }

            foreach (var baseline in baselines)
                await ReplaceInventoryBaselineAsync(connection, transaction, baseline.ScopeKey, baseline.Revision, baseline.Payload, cancellationToken).ConfigureAwait(false);

            await using (var metadata = connection.CreateCommand())
            {
                metadata.Transaction = (SqliteTransaction)transaction;
                metadata.CommandText = """
                    UPDATE observation_metadata SET value = '2' WHERE key = 'schema_minor';
                    UPDATE observation_metadata SET value = '2' WHERE key = 'minimum_writer_capability';
                    PRAGMA user_version = 1002;
                    """;
                await metadata.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            options.BeforeMigrationCommit?.Invoke();
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            migrationLock.Unlock(0, 1);
        }
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
                scope_key, owner_local_content_id, owner_home_world_id, container_kind,
                revision, observed_at_utc, scope_json, capture_json, source_order_capture_json,
                payload_contract, payload_version, payload_json, payload_sha256,
                is_stale, stale_reason, stale_observed_at_utc, last_confirmed_at_utc, confirmation_count)
            VALUES(
                $scope_key, $owner_local_content_id, $owner_home_world_id, $container_kind,
                $revision, $observed_at, $scope_json, $capture_json, $capture_json,
                $payload_contract, $payload_version, $payload_json, $payload_sha256,
                0, NULL, NULL, $observed_at, 1)
            ON CONFLICT(scope_key) DO UPDATE SET
                revision = excluded.revision,
                owner_local_content_id = excluded.owner_local_content_id,
                owner_home_world_id = excluded.owner_home_world_id,
                container_kind = excluded.container_kind,
                observed_at_utc = excluded.observed_at_utc,
                scope_json = excluded.scope_json,
                capture_json = excluded.capture_json,
                source_order_capture_json = excluded.source_order_capture_json,
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
        command.Parameters.AddWithValue("$owner_local_content_id", FormatOwnerId(observation.Scope.Owner.LocalContentId));
        command.Parameters.AddWithValue("$owner_home_world_id", observation.Scope.Owner.HomeWorldId);
        command.Parameters.AddWithValue("$container_kind", (int)observation.Scope.Container);
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

    private static async ValueTask ReplaceInventoryBaselineAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        string scopeKey,
        long storeRevision,
        InventoryObservationPayload payload,
        CancellationToken cancellationToken)
    {
        await using (var clear = connection.CreateCommand())
        {
            clear.Transaction = (SqliteTransaction)transaction;
            clear.CommandText = """
                DELETE FROM inventory_slot_change WHERE scope_key = $scope_key;
                DELETE FROM inventory_slot_projection WHERE scope_key = $scope_key;
                DELETE FROM inventory_scope_projection WHERE scope_key = $scope_key;
                """;
            clear.Parameters.AddWithValue("$scope_key", scopeKey);
            await clear.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var scope = connection.CreateCommand())
        {
            scope.Transaction = (SqliteTransaction)transaction;
            scope.CommandText = """
                INSERT INTO inventory_scope_projection(
                    scope_key, base_revision, requested_container_ids_json, observed_container_ids_json)
                VALUES($scope_key, $base_revision, $requested, $observed);
                """;
            scope.Parameters.AddWithValue("$scope_key", scopeKey);
            scope.Parameters.AddWithValue("$base_revision", storeRevision);
            scope.Parameters.AddWithValue("$requested", JsonSerializer.Serialize(payload.RequestedContainerIds, JsonOptions));
            scope.Parameters.AddWithValue("$observed", JsonSerializer.Serialize(payload.ObservedContainerIds, JsonOptions));
            await scope.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var item in payload.Items)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = (SqliteTransaction)transaction;
            insert.CommandText = """
                INSERT INTO inventory_slot_projection(
                    scope_key, container_id, slot_index, item_id, quantity, is_high_quality)
                VALUES($scope_key, $container_id, $slot_index, $item_id, $quantity, $is_high_quality);
                """;
            insert.Parameters.AddWithValue("$scope_key", scopeKey);
            insert.Parameters.AddWithValue("$container_id", item.ContainerId);
            insert.Parameters.AddWithValue("$slot_index", item.SlotIndex);
            insert.Parameters.AddWithValue("$item_id", item.ItemId);
            insert.Parameters.AddWithValue("$quantity", item.Quantity);
            insert.Parameters.AddWithValue("$is_high_quality", item.IsHighQuality);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async ValueTask<HashSet<int>?> ReadObservedInventoryContainersAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        string scopeKey,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = "SELECT observed_container_ids_json FROM inventory_scope_projection WHERE scope_key = $scope_key;";
        command.Parameters.AddWithValue("$scope_key", scopeKey);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (value is null or DBNull)
            return null;
        return (JsonSerializer.Deserialize<int[]>((string)value, JsonOptions)
                ?? throw new InvalidDataException("Stored observed inventory containers are null."))
            .ToHashSet();
    }

    private static async ValueTask<InventorySlotValue?> ReadInventorySlotAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        string scopeKey,
        int containerId,
        int slotIndex,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            SELECT item_id, quantity, is_high_quality
            FROM inventory_slot_projection
            WHERE scope_key = $scope_key AND container_id = $container_id AND slot_index = $slot_index;
            """;
        command.Parameters.AddWithValue("$scope_key", scopeKey);
        command.Parameters.AddWithValue("$container_id", containerId);
        command.Parameters.AddWithValue("$slot_index", slotIndex);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new InventorySlotValue(checked((uint)reader.GetInt64(0)), reader.GetInt32(1), reader.GetBoolean(2))
            : null;
    }

    private static async ValueTask ApplyInventorySlotChangeAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        string scopeKey,
        long storeRevision,
        ObservationCapture capture,
        InventorySlotChange change,
        CancellationToken cancellationToken)
    {
        await using (var projection = connection.CreateCommand())
        {
            projection.Transaction = (SqliteTransaction)transaction;
            projection.CommandText = change.Current is null
                ? "DELETE FROM inventory_slot_projection WHERE scope_key = $scope_key AND container_id = $container_id AND slot_index = $slot_index;"
                : """
                    INSERT INTO inventory_slot_projection(
                        scope_key, container_id, slot_index, item_id, quantity, is_high_quality)
                    VALUES($scope_key, $container_id, $slot_index, $item_id, $quantity, $is_high_quality)
                    ON CONFLICT(scope_key, container_id, slot_index) DO UPDATE SET
                        item_id = excluded.item_id,
                        quantity = excluded.quantity,
                        is_high_quality = excluded.is_high_quality;
                    """;
            projection.Parameters.AddWithValue("$scope_key", scopeKey);
            projection.Parameters.AddWithValue("$container_id", change.ContainerId);
            projection.Parameters.AddWithValue("$slot_index", change.SlotIndex);
            if (change.Current is { } current)
            {
                projection.Parameters.AddWithValue("$item_id", current.ItemId);
                projection.Parameters.AddWithValue("$quantity", current.Quantity);
                projection.Parameters.AddWithValue("$is_high_quality", current.IsHighQuality);
            }
            await projection.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var history = connection.CreateCommand();
        history.Transaction = (SqliteTransaction)transaction;
        history.CommandText = """
            INSERT INTO inventory_slot_change(
                store_revision, scope_key, capture_json, container_id, slot_index,
                previous_item_id, previous_quantity, previous_is_high_quality,
                current_item_id, current_quantity, current_is_high_quality)
            VALUES(
                $store_revision, $scope_key, $capture_json, $container_id, $slot_index,
                $previous_item_id, $previous_quantity, $previous_is_high_quality,
                $current_item_id, $current_quantity, $current_is_high_quality);
            """;
        history.Parameters.AddWithValue("$store_revision", storeRevision);
        history.Parameters.AddWithValue("$scope_key", scopeKey);
        history.Parameters.AddWithValue("$capture_json", JsonSerializer.Serialize(capture, JsonOptions));
        history.Parameters.AddWithValue("$container_id", change.ContainerId);
        history.Parameters.AddWithValue("$slot_index", change.SlotIndex);
        AddSlotParameters(history, "previous", change.Previous);
        AddSlotParameters(history, "current", change.Current);
        await history.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void AddSlotParameters(SqliteCommand command, string prefix, InventorySlotValue? value)
    {
        command.Parameters.AddWithValue($"${prefix}_item_id", value is null ? DBNull.Value : value.ItemId);
        command.Parameters.AddWithValue($"${prefix}_quantity", value is null ? DBNull.Value : value.Quantity);
        command.Parameters.AddWithValue($"${prefix}_is_high_quality", value is null ? DBNull.Value : value.IsHighQuality);
    }

    private static async ValueTask AdvanceInventoryCurrentAsync(
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
                source_order_capture_json = $capture_json,
                payload_sha256 = $payload_sha256,
                is_stale = 0,
                stale_reason = NULL,
                stale_observed_at_utc = NULL,
                last_confirmed_at_utc = $observed_at,
                confirmation_count = 1
            WHERE scope_key = $scope_key;
            """;
        command.Parameters.AddWithValue("$revision", storeRevision);
        command.Parameters.AddWithValue("$observed_at", FormatUtc(capture.ObservedAtUtc));
        command.Parameters.AddWithValue("$capture_json", JsonSerializer.Serialize(capture, JsonOptions));
        command.Parameters.AddWithValue("$payload_sha256", Hash($"inventory-delta:{storeRevision.ToString(CultureInfo.InvariantCulture)}"));
        command.Parameters.AddWithValue("$scope_key", scopeKey);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask InsertInventoryDeltaHistoryAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        InventoryObservationDelta observation,
        ObservationValidationResult validation,
        long storeRevision,
        CancellationToken cancellationToken)
    {
        var payload = ObservationPayload.Create(ObservationPayloadContracts.InventorySlotDelta, ObservationPayloadContracts.Version, observation.Updates);
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
        command.Parameters.AddWithValue("$store_revision", storeRevision);
        command.Parameters.AddWithValue("$observed_at", FormatUtc(observation.Capture.ObservedAtUtc));
        command.Parameters.AddWithValue("$status", (int)validation.Status);
        command.Parameters.AddWithValue("$code", (int)validation.Code);
        command.Parameters.AddWithValue("$message", validation.Message);
        command.Parameters.AddWithValue("$scope_json", JsonSerializer.Serialize(observation.Scope, JsonOptions));
        command.Parameters.AddWithValue("$capture_json", JsonSerializer.Serialize(observation.Capture, JsonOptions));
        command.Parameters.AddWithValue("$payload_contract", payload.Contract);
        command.Parameters.AddWithValue("$payload_version", payload.Version);
        command.Parameters.AddWithValue("$payload_json", payload.Json);
        command.Parameters.AddWithValue("$payload_sha256", Hash(payload.Json));
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
                source_order_capture_json = $capture_json,
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
        ObservationCapture? sourceOrderCapture,
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            UPDATE current_projection SET
                revision = $revision,
                source_order_capture_json = COALESCE($source_order_capture_json, source_order_capture_json),
                is_stale = 1,
                stale_reason = $reason,
                stale_observed_at_utc = $observed_at
            WHERE scope_key = $scope_key;
            """;
        command.Parameters.AddWithValue("$reason", reason);
        command.Parameters.AddWithValue("$revision", storeRevision);
        command.Parameters.AddWithValue(
            "$source_order_capture_json",
            sourceOrderCapture is null ? DBNull.Value : JsonSerializer.Serialize(sourceOrderCapture, JsonOptions));
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
            SELECT revision, scope_json, capture_json, source_order_capture_json, payload_contract, payload_version,
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
            reader.GetString(4),
            reader.GetInt32(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.GetBoolean(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            reader.GetString(11),
            reader.GetInt32(12));
    }

    private static async ValueTask<List<CurrentRow>> ReadCurrentRowsByOwnerAsync(
        SqliteConnection connection,
        ObservationOwner owner,
        ObservationContainerKind container,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT revision, scope_json, capture_json, source_order_capture_json, payload_contract, payload_version,
                   payload_json, payload_sha256, is_stale, stale_reason,
                   stale_observed_at_utc, last_confirmed_at_utc, confirmation_count
            FROM current_projection
            WHERE owner_local_content_id = $owner_local_content_id
              AND owner_home_world_id = $owner_home_world_id
              AND container_kind = $container_kind
            ORDER BY scope_key;
            """;
        command.Parameters.AddWithValue("$owner_local_content_id", FormatOwnerId(owner.LocalContentId));
        command.Parameters.AddWithValue("$owner_home_world_id", owner.HomeWorldId);
        command.Parameters.AddWithValue("$container_kind", (int)container);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var rows = new List<CurrentRow>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new CurrentRow(
                reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetInt32(5),
                reader.GetString(6), reader.GetString(7), reader.GetBoolean(8), reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetString(10), reader.GetString(11), reader.GetInt32(12)));
        }
        return rows;
    }

    private static async ValueTask<TrustedObservation> ToTrustedObservationAsync(
        SqliteConnection connection,
        CurrentRow row,
        CancellationToken cancellationToken)
    {
        var scope = JsonSerializer.Deserialize<ObservationScope>(row.ScopeJson, JsonOptions)
            ?? throw new InvalidDataException("Stored observation scope is null.");
        var payload = IsInventoryContainer(scope.Container)
            ? await ReadInventoryPayloadAsync(connection, null, CreateScopeKey(scope), row.PayloadContract, row.PayloadVersion, cancellationToken).ConfigureAwait(false)
            : new ObservationPayload(row.PayloadContract, row.PayloadVersion, row.PayloadJson);
        return new TrustedObservation(
            row.Revision,
            scope,
            JsonSerializer.Deserialize<ObservationCapture>(row.CaptureJson, JsonOptions)
                ?? throw new InvalidDataException("Stored observation capture is null."),
            payload,
            row.IsStale,
            row.StaleReason,
            ParseNullableUtc(row.StaleObservedAtUtc),
            ParseUtc(row.LastConfirmedAtUtc),
            row.ConfirmationCount);
    }

    private static async ValueTask<ObservationPayload> ReadInventoryPayloadAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction? transaction,
        string scopeKey,
        string payloadContract,
        int payloadVersion,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<int> requested;
        IReadOnlyList<int> observed;
        await using (var scope = connection.CreateCommand())
        {
            scope.Transaction = (SqliteTransaction?)transaction;
            scope.CommandText = """
                SELECT requested_container_ids_json, observed_container_ids_json
                FROM inventory_scope_projection
                WHERE scope_key = $scope_key;
                """;
            scope.Parameters.AddWithValue("$scope_key", scopeKey);
            await using var reader = await scope.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                throw new InvalidDataException("The normalized inventory baseline is missing for a trusted inventory scope.");
            requested = JsonSerializer.Deserialize<int[]>(reader.GetString(0), JsonOptions)
                ?? throw new InvalidDataException("Stored requested inventory containers are null.");
            observed = JsonSerializer.Deserialize<int[]>(reader.GetString(1), JsonOptions)
                ?? throw new InvalidDataException("Stored observed inventory containers are null.");
        }

        var items = new List<InventoryItemObservation>();
        await using (var slots = connection.CreateCommand())
        {
            slots.Transaction = (SqliteTransaction?)transaction;
            slots.CommandText = """
                SELECT container_id, slot_index, item_id, quantity, is_high_quality
                FROM inventory_slot_projection
                WHERE scope_key = $scope_key
                ORDER BY container_id, slot_index;
                """;
            slots.Parameters.AddWithValue("$scope_key", scopeKey);
            await using var reader = await slots.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                items.Add(new InventoryItemObservation(
                    reader.GetInt32(0),
                    reader.GetInt32(1),
                    checked((uint)reader.GetInt64(2)),
                    reader.GetInt32(3),
                    reader.GetBoolean(4)));
            }
        }

        return ObservationPayload.Create(
            payloadContract,
            payloadVersion,
            new InventoryObservationPayload(requested, observed, items));
    }

    private static async ValueTask<long> ReadMetadataLongAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        string key,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = "SELECT value FROM observation_metadata WHERE key = $key;";
        command.Parameters.AddWithValue("$key", key);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (value is null or DBNull)
            throw new InvalidDataException($"Observation database metadata '{key}' is missing.");
        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static async ValueTask<List<long>> ReadInventoryBaselinesAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        ObservationOwner owner,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            SELECT inventory.base_revision
            FROM inventory_scope_projection inventory
            JOIN current_projection current ON current.scope_key = inventory.scope_key
            WHERE current.owner_local_content_id = $owner_local_content_id
              AND current.owner_home_world_id = $owner_home_world_id
            ORDER BY inventory.scope_key;
            """;
        command.Parameters.AddWithValue("$owner_local_content_id", FormatOwnerId(owner.LocalContentId));
        command.Parameters.AddWithValue("$owner_home_world_id", owner.HomeWorldId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var revisions = new List<long>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            revisions.Add(reader.GetInt64(0));
        return revisions;
    }

    private static async ValueTask<List<InventoryChangeBatch>> ReadInventoryChangeBatchesAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        ObservationOwner owner,
        long afterRevision,
        long currentRevision,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            SELECT changes.store_revision, current.scope_json, changes.capture_json,
                   changes.container_id, changes.slot_index,
                   changes.previous_item_id, changes.previous_quantity, changes.previous_is_high_quality,
                   changes.current_item_id, changes.current_quantity, changes.current_is_high_quality
            FROM inventory_slot_change changes
            JOIN current_projection current ON current.scope_key = changes.scope_key
            WHERE current.owner_local_content_id = $owner_local_content_id
              AND current.owner_home_world_id = $owner_home_world_id
              AND changes.store_revision > $after_revision
              AND changes.store_revision <= $current_revision
            ORDER BY changes.store_revision, changes.scope_key, changes.container_id, changes.slot_index;
            """;
        command.Parameters.AddWithValue("$owner_local_content_id", FormatOwnerId(owner.LocalContentId));
        command.Parameters.AddWithValue("$owner_home_world_id", owner.HomeWorldId);
        command.Parameters.AddWithValue("$after_revision", afterRevision);
        command.Parameters.AddWithValue("$current_revision", currentRevision);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var rows = new List<(long Revision, ObservationScope Scope, ObservationCapture Capture, InventorySlotChange Change)>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var scope = JsonSerializer.Deserialize<ObservationScope>(reader.GetString(1), JsonOptions)
                ?? throw new InvalidDataException("Stored inventory change scope is null.");
            var capture = JsonSerializer.Deserialize<ObservationCapture>(reader.GetString(2), JsonOptions)
                ?? throw new InvalidDataException("Stored inventory change capture is null.");
            rows.Add((
                reader.GetInt64(0),
                scope,
                capture,
                new InventorySlotChange(
                    reader.GetInt32(3),
                    reader.GetInt32(4),
                    ReadSlotValue(reader, 5),
                    ReadSlotValue(reader, 8))));
        }

        return rows
            .GroupBy(row => (row.Revision, row.Scope, row.Capture))
            .Select(group => new InventoryChangeBatch(
                group.Key.Revision,
                group.Key.Scope,
                group.Key.Capture,
                group.Select(row => row.Change).ToArray()))
            .ToList();
    }

    private static InventorySlotValue? ReadSlotValue(SqliteDataReader reader, int offset) =>
        reader.IsDBNull(offset)
            ? null
            : new InventorySlotValue(
                checked((uint)reader.GetInt64(offset)),
                reader.GetInt32(offset + 1),
                reader.GetBoolean(offset + 2));

    private static ObservationValidationResult ValidateInventoryDelta(
        InventoryObservationDelta observation,
        out ObservationEnvelope validationEnvelope)
    {
        var updates = observation.Updates ?? [];
        var containers = updates.Select(update => update.ContainerId).Distinct().Order().ToArray();
        var payloadContract = observation.Scope.Container switch
        {
            ObservationContainerKind.PlayerInventory => ObservationPayloadContracts.PlayerInventory,
            ObservationContainerKind.RetainerInventory => ObservationPayloadContracts.RetainerInventory,
            ObservationContainerKind.Saddlebag => ObservationPayloadContracts.Saddlebag,
            _ => ObservationPayloadContracts.InventorySlotDelta,
        };
        var payload = new InventoryObservationPayload(
            containers,
            containers,
            updates
                .Where(update => update.Current is not null)
                .Select(update => new InventoryItemObservation(
                    update.ContainerId,
                    update.SlotIndex,
                    update.Current!.ItemId,
                    update.Current.Quantity,
                    update.Current.IsHighQuality))
                .ToArray());
        validationEnvelope = new ObservationEnvelope(
            observation.Scope,
            observation.Capture,
            ObservationPayload.Create(payloadContract, ObservationPayloadContracts.Version, payload));
        if (!IsInventoryContainer(observation.Scope.Container))
            return new ObservationValidationResult(ObservationValidationStatus.Invalid, ObservationValidationCode.ContainerSubjectMismatch, "Slot changes require an inventory observation scope.");
        if (updates.Count == 0)
            return new ObservationValidationResult(ObservationValidationStatus.Invalid, ObservationValidationCode.PayloadInvalid, "An inventory delta must name at least one changed slot.");
        if (updates.Any(update => update.SlotIndex < 0 || update.Current is { ItemId: 0 } or { Quantity: <= 0 }) ||
            updates.Select(update => (update.ContainerId, update.SlotIndex)).Distinct().Count() != updates.Count)
        {
            return new ObservationValidationResult(ObservationValidationStatus.Invalid, ObservationValidationCode.PayloadInvalid, "An inventory delta contains an invalid or duplicated slot update.");
        }
        return ObservationValidator.Validate(validationEnvelope);
    }

    private static bool IsInventoryContainer(ObservationContainerKind container) =>
        container is ObservationContainerKind.PlayerInventory or ObservationContainerKind.RetainerInventory or ObservationContainerKind.Saddlebag;

    private static int CompareSourceOrder(ObservationCapture incoming, CurrentRow current)
    {
        var existing = JsonSerializer.Deserialize<ObservationCapture>(current.SourceOrderCaptureJson, JsonOptions)
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
        var revision = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
        await using var changeCommand = connection.CreateCommand();
        changeCommand.Transaction = (SqliteTransaction)transaction;
        changeCommand.CommandText = "UPDATE observation_metadata SET value = $revision WHERE key = 'change_revision';";
        changeCommand.Parameters.AddWithValue("$revision", revision.ToString(CultureInfo.InvariantCulture));
        await changeCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return revision;
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
        SignalCrossCopyChange(change.Revision);
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

    private void SignalCrossCopyChange(long revision)
    {
        var signalPath = Path.GetFullPath(options.ChangeSignalPath ??
            Path.Combine(Path.GetDirectoryName(options.DatabasePath)!, "changes.signal"));
        var temporaryPath = Path.Combine(Path.GetDirectoryName(signalPath)!, $".{Path.GetFileName(signalPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(signalPath)!);
            File.WriteAllText(temporaryPath, revision.ToString(CultureInfo.InvariantCulture), Encoding.ASCII);
            File.Move(temporaryPath, signalPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LastNotificationError = ex.Message;
            try { File.Delete(temporaryPath); }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
        }
    }

    private static string CreateScopeKey(ObservationScope scope) => string.Create(
        CultureInfo.InvariantCulture,
        $"{scope.Owner.LocalContentId:X16}:{scope.Owner.HomeWorldId}:{(int)scope.Subject.Kind}:{scope.Subject.Id:X16}:{(int)scope.Container}");
    private static string FormatOwnerId(ulong value) => value.ToString("X16", CultureInfo.InvariantCulture);

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

    private static async ValueTask<int> ReadMetadataIntAsync(
        SqliteConnection connection,
        string key,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM observation_metadata WHERE key = $key;";
        command.Parameters.AddWithValue("$key", key);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (value is null or DBNull)
            throw new InvalidDataException($"Observation database metadata '{key}' is missing.");
        return Convert.ToInt32(value, CultureInfo.InvariantCulture);
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
        string SourceOrderCaptureJson,
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
