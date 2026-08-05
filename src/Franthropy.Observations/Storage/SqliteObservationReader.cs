using System.Globalization;
using System.Text.Json;
using Franthropy.Observations.V1;
using Microsoft.Data.Sqlite;

namespace Franthropy.Observations.Storage;

public sealed class SqliteObservationReader : IObservationReader, IInventoryObservationReader, IAsyncDisposable
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
            ObservationDatabaseProbeStatus.Compatible =>
                new ObservationReaderOpenResult(ObservationStoreOpenStatus.Ready, new SqliteObservationReader(normalized), probe.Message, probe),
            ObservationDatabaseProbeStatus.UpgradeRequired =>
                new ObservationReaderOpenResult(ObservationStoreOpenStatus.UpgradeRequired, null, probe.Message, probe),
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
                Cache = SqliteCacheMode.Private,
                DefaultTimeout = Math.Max(1, (int)Math.Ceiling(options.BusyTimeout.TotalSeconds)),
            };
            await using var connection = new SqliteConnection(builder.ToString());
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                SELECT revision, scope_json, capture_json, payload_contract, payload_version,
                       payload_json, is_stale, stale_reason, stale_observed_at_utc,
                       last_confirmed_at_utc, confirmation_count
                FROM current_projection
                WHERE scope_key = $scope_key;
                """;
            command.Parameters.AddWithValue("$scope_key", CreateScopeKey(scope));
            var row = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await row.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                await row.DisposeAsync().ConfigureAwait(false);
                return new ObservationReadResult(ObservationReadStatus.NotObserved, null, "No trusted observation exists for this scope.");
            }
            var raw = ReadRawObservation(row);
            await row.DisposeAsync().ConfigureAwait(false);
            var observation = await MaterializeAsync(connection, transaction, raw, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
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
                Cache = SqliteCacheMode.Private,
                DefaultTimeout = Math.Max(1, (int)Math.Ceiling(options.BusyTimeout.TotalSeconds)),
            };
            await using var connection = new SqliteConnection(builder.ToString());
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
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
            var row = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var raw = new List<RawObservation>();
            while (await row.ReadAsync(cancellationToken).ConfigureAwait(false))
                raw.Add(ReadRawObservation(row));
            await row.DisposeAsync().ConfigureAwait(false);
            var observations = new List<TrustedObservation>(raw.Count);
            foreach (var observation in raw)
                observations.Add(await MaterializeAsync(connection, transaction, observation, cancellationToken).ConfigureAwait(false));
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
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
            await using var connection = CreateReadConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var currentRevision = await ReadMetadataLongAsync(connection, transaction, "next_revision", cancellationToken).ConfigureAwait(false);
            var baselines = await ReadInventoryBaselinesAsync(connection, transaction, owner, cancellationToken).ConfigureAwait(false);
            if (baselines.Count == 0)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new InventoryChangeReadResult(InventoryChangeReadStatus.NotObserved, currentRevision, null, [], "No trusted inventory observation exists for this owner.");
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

            var batches = await ReadInventoryChangeBatchesAsync(connection, transaction, owner, afterRevision, currentRevision, cancellationToken).ConfigureAwait(false);
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
        catch (SqliteException ex) when (ex.SqliteErrorCode is 5 or 6)
        {
            return new InventoryChangeReadResult(InventoryChangeReadStatus.Busy, afterRevision, null, [], "The observation database remained busy beyond the bounded wait.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException or JsonException)
        {
            return new InventoryChangeReadResult(InventoryChangeReadStatus.Unavailable, afterRevision, null, [], ex.Message);
        }
    }

    public ValueTask DisposeAsync()
    {
        disposed = true;
        return ValueTask.CompletedTask;
    }

    private SqliteConnection CreateReadConnection()
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = options.DatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            DefaultTimeout = Math.Max(1, (int)Math.Ceiling(options.BusyTimeout.TotalSeconds)),
        };
        return new SqliteConnection(builder.ToString());
    }

    private static string CreateScopeKey(ObservationScope scope) => string.Create(
        CultureInfo.InvariantCulture,
        $"{scope.Owner.LocalContentId:X16}:{scope.Owner.HomeWorldId}:{(int)scope.Subject.Kind}:{scope.Subject.Id:X16}:{(int)scope.Container}");

    private static DateTimeOffset ParseUtc(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();

    private static RawObservation ReadRawObservation(SqliteDataReader row) => new(
        row.GetInt64(0),
        JsonSerializer.Deserialize<ObservationScope>(row.GetString(1), JsonOptions)
            ?? throw new InvalidDataException("Stored observation scope is null."),
        JsonSerializer.Deserialize<ObservationCapture>(row.GetString(2), JsonOptions)
            ?? throw new InvalidDataException("Stored observation capture is null."),
        row.GetString(3),
        row.GetInt32(4),
        row.GetString(5),
        row.GetBoolean(6),
        row.IsDBNull(7) ? null : row.GetString(7),
        row.IsDBNull(8) ? null : ParseUtc(row.GetString(8)),
        ParseUtc(row.GetString(9)),
        row.GetInt32(10));

    private static async ValueTask<TrustedObservation> MaterializeAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        RawObservation raw,
        CancellationToken cancellationToken)
    {
        var payload = IsInventoryContainer(raw.Scope.Container)
            ? await ReadInventoryPayloadAsync(connection, transaction, CreateScopeKey(raw.Scope), raw.PayloadContract, raw.PayloadVersion, cancellationToken).ConfigureAwait(false)
            : new ObservationPayload(raw.PayloadContract, raw.PayloadVersion, raw.PayloadJson);
        return new TrustedObservation(
            raw.Revision,
            raw.Scope,
            raw.Capture,
            payload,
            raw.IsStale,
            raw.StaleReason,
            raw.StaleObservedAtUtc,
            raw.LastConfirmedAtUtc,
            raw.ConfirmationCount);
    }

    private static async ValueTask<ObservationPayload> ReadInventoryPayloadAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        string scopeKey,
        string contract,
        int version,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<int> requested;
        IReadOnlyList<int> observed;
        await using (var scope = connection.CreateCommand())
        {
            scope.Transaction = (SqliteTransaction)transaction;
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
            slots.Transaction = (SqliteTransaction)transaction;
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
                    reader.GetInt32(0), reader.GetInt32(1), checked((uint)reader.GetInt64(2)), reader.GetInt32(3), reader.GetBoolean(4)));
            }
        }
        return ObservationPayload.Create(contract, version, new InventoryObservationPayload(requested, observed, items));
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
        command.Parameters.AddWithValue("$owner_local_content_id", owner.LocalContentId.ToString("X16", CultureInfo.InvariantCulture));
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
        command.Parameters.AddWithValue("$owner_local_content_id", owner.LocalContentId.ToString("X16", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$owner_home_world_id", owner.HomeWorldId);
        command.Parameters.AddWithValue("$after_revision", afterRevision);
        command.Parameters.AddWithValue("$current_revision", currentRevision);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var rows = new List<(long Revision, ObservationScope Scope, ObservationCapture Capture, InventorySlotChange Change)>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add((
                reader.GetInt64(0),
                JsonSerializer.Deserialize<ObservationScope>(reader.GetString(1), JsonOptions)
                    ?? throw new InvalidDataException("Stored inventory change scope is null."),
                JsonSerializer.Deserialize<ObservationCapture>(reader.GetString(2), JsonOptions)
                    ?? throw new InvalidDataException("Stored inventory change capture is null."),
                new InventorySlotChange(reader.GetInt32(3), reader.GetInt32(4), ReadSlotValue(reader, 5), ReadSlotValue(reader, 8))));
        }
        return rows
            .GroupBy(row => (row.Revision, row.Scope, row.Capture))
            .Select(group => new InventoryChangeBatch(group.Key.Revision, group.Key.Scope, group.Key.Capture, group.Select(row => row.Change).ToArray()))
            .ToList();
    }

    private static InventorySlotValue? ReadSlotValue(SqliteDataReader reader, int offset) =>
        reader.IsDBNull(offset)
            ? null
            : new InventorySlotValue(checked((uint)reader.GetInt64(offset)), reader.GetInt32(offset + 1), reader.GetBoolean(offset + 2));

    private static bool IsInventoryContainer(ObservationContainerKind container) =>
        container is ObservationContainerKind.PlayerInventory or ObservationContainerKind.RetainerInventory or ObservationContainerKind.Saddlebag;

    private sealed record RawObservation(
        long Revision,
        ObservationScope Scope,
        ObservationCapture Capture,
        string PayloadContract,
        int PayloadVersion,
        string PayloadJson,
        bool IsStale,
        string? StaleReason,
        DateTimeOffset? StaleObservedAtUtc,
        DateTimeOffset LastConfirmedAtUtc,
        int ConfirmationCount);
}
