using Franthropy.Observations.Storage;
using Franthropy.Observations.V1;
using Microsoft.Data.Sqlite;

namespace Franthropy.Observations.Tests;

public sealed class SqliteObservationStoreTests
{
    [Fact]
    public void Default_native_sqlite_floor_matches_store_features()
    {
        var options = new ObservationStoreOptions { DatabasePath = "unused.db" };

        Assert.Equal(new Version(3, 35, 0), options.MinimumNativeSqliteVersion);
    }

    [Fact]
    public async Task Inventory_delta_updates_only_named_slots_and_exposes_exact_change_batch()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var baseline = fixture.CreatePlayerInventoryObservation(1,
        [
            new InventoryItemObservation(1, 0, 100, 4, false),
            new InventoryItemObservation(1, 1, 200, 7, true),
            new InventoryItemObservation(2, 0, 300, 9, false),
        ]);
        var baselineWrite = await fixture.Store.WriteAsync(baseline);
        var otherOwner = new ObservationOwner(999, 74);
        var otherBaseline = fixture.CreatePlayerInventoryObservation(1, [new InventoryItemObservation(1, 0, 900, 12, false)]) with
        {
            Scope = new ObservationScope(otherOwner, ObservationSubject.Character(otherOwner), ObservationContainerKind.PlayerInventory),
        };
        await fixture.Store.WriteAsync(otherBaseline);
        var delta = fixture.CreateInventoryDelta(2,
        [
            new InventorySlotUpdate(1, 0, new InventorySlotValue(100, 2, false)),
            new InventorySlotUpdate(1, 1, null),
        ]);

        var deltaWrite = await fixture.Store.WriteInventoryDeltaAsync(delta);
        var current = await fixture.Store.ReadCurrentAsync(baseline.Scope);
        var otherCurrent = await fixture.Store.ReadCurrentAsync(otherBaseline.Scope);
        var changes = await fixture.Store.ReadInventoryChangesAsync(baseline.Scope.Owner, baselineWrite.CurrentRevision!.Value);
        var payload = current.Observation!.Payload.Deserialize<InventoryObservationPayload>(ObservationPayloadContracts.PlayerInventory, 1);
        var otherPayload = otherCurrent.Observation!.Payload.Deserialize<InventoryObservationPayload>(ObservationPayloadContracts.PlayerInventory, 1);
        var readerOpen = await SqliteObservationReader.OpenAsync(new ObservationStoreOptions { DatabasePath = fixture.DatabasePath });
        Assert.True(readerOpen.IsReady, readerOpen.Message);
        var readerCurrent = await readerOpen.Reader!.ReadCurrentAsync(baseline.Scope);
        var readerChanges = await readerOpen.Reader.ReadInventoryChangesAsync(baseline.Scope.Owner, baselineWrite.CurrentRevision.Value);

        Assert.Equal(ObservationWriteStatus.AcceptedChanged, deltaWrite.Status);
        Assert.Equal([(1, 0, 100U, 2), (2, 0, 300U, 9)], payload.Items.Select(item => (item.ContainerId, item.SlotIndex, item.ItemId, item.Quantity)));
        Assert.Equal((900U, 12), (Assert.Single(otherPayload.Items).ItemId, Assert.Single(otherPayload.Items).Quantity));
        Assert.Equal(InventoryChangeReadStatus.Found, changes.Status);
        var batch = Assert.Single(changes.Batches);
        Assert.Equal(deltaWrite.CurrentRevision, batch.Revision);
        Assert.Equal(2, batch.Changes.Count);
        Assert.Equal(new InventorySlotValue(200, 7, true), batch.Changes.Single(change => change.SlotIndex == 1).Previous);
        Assert.Null(batch.Changes.Single(change => change.SlotIndex == 1).Current);
        Assert.Equal(2, readerCurrent.Observation!.Payload.Deserialize<InventoryObservationPayload>(ObservationPayloadContracts.PlayerInventory, 1).Items.Single(item => item.ItemId == 100).Quantity);
        Assert.Equal(InventoryChangeReadStatus.Found, readerChanges.Status);
        await readerOpen.Reader.DisposeAsync();
    }

    [Fact]
    public async Task Inventory_delta_replay_and_older_revision_preserve_newer_slots()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var baseline = fixture.CreatePlayerInventoryObservation(1, [new InventoryItemObservation(1, 0, 100, 4, false)]);
        await fixture.Store.WriteAsync(baseline);
        var current = fixture.CreateInventoryDelta(3, [new InventorySlotUpdate(1, 0, new InventorySlotValue(100, 8, false))]);
        await fixture.Store.WriteInventoryDeltaAsync(current);

        var repeated = await fixture.Store.WriteInventoryDeltaAsync(current);
        var older = await fixture.Store.WriteInventoryDeltaAsync(
            fixture.CreateInventoryDelta(2, [new InventorySlotUpdate(1, 0, new InventorySlotValue(100, 1, false))]));
        var read = await fixture.Store.ReadCurrentAsync(baseline.Scope);
        var item = Assert.Single(read.Observation!.Payload.Deserialize<InventoryObservationPayload>(ObservationPayloadContracts.PlayerInventory, 1).Items);

        Assert.Equal(ObservationWriteStatus.IgnoredRepeatedRevision, repeated.Status);
        Assert.Equal(ObservationWriteStatus.IgnoredOlderRevision, older.Status);
        Assert.Equal(8, item.Quantity);
    }

    [Fact]
    public async Task Inventory_delta_requires_a_fresh_baseline_after_invalidation()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var baseline = fixture.CreatePlayerInventoryObservation(1, [new InventoryItemObservation(1, 0, 100, 4, false)]);
        await fixture.Store.WriteAsync(baseline);
        await fixture.Store.InvalidateAsync(baseline.Scope, "synthetic uncertainty", baseline.Capture.ObservedAtUtc.AddMinutes(1));

        var delta = await fixture.Store.WriteInventoryDeltaAsync(
            fixture.CreateInventoryDelta(2, [new InventorySlotUpdate(1, 0, new InventorySlotValue(100, 9, false))]));
        var current = await fixture.Store.ReadCurrentAsync(baseline.Scope);

        Assert.Equal(ObservationWriteStatus.Rejected, delta.Status);
        Assert.True(current.Observation!.IsStale);
        Assert.Equal(4, Assert.Single(current.Observation.Payload.Deserialize<InventoryObservationPayload>(
            ObservationPayloadContracts.PlayerInventory,
            ObservationPayloadContracts.Version).Items).Quantity);
    }

    [Fact]
    public async Task Inventory_delta_rejects_a_container_absent_from_the_baseline()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var baseline = fixture.CreatePlayerInventoryObservation(1, [new InventoryItemObservation(1, 0, 100, 4, false)]);
        await fixture.Store.WriteAsync(baseline);

        var delta = await fixture.Store.WriteInventoryDeltaAsync(
            fixture.CreateInventoryDelta(2, [new InventorySlotUpdate(3, 0, new InventorySlotValue(300, 1, false))]));
        var current = await fixture.Store.ReadCurrentAsync(baseline.Scope);
        var payload = current.Observation!.Payload.Deserialize<InventoryObservationPayload>(
            ObservationPayloadContracts.PlayerInventory,
            ObservationPayloadContracts.Version);

        Assert.Equal(ObservationWriteStatus.Rejected, delta.Status);
        Assert.DoesNotContain(payload.Items, item => item.ContainerId == 3);
        Assert.DoesNotContain(3, payload.ObservedContainerIds);
    }

    [Fact]
    public async Task New_full_inventory_baseline_requires_snapshot_and_converges_after_deltas()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var baseline = fixture.CreatePlayerInventoryObservation(1, [new InventoryItemObservation(1, 0, 100, 4, false)]);
        var first = await fixture.Store.WriteAsync(baseline);
        await fixture.Store.WriteInventoryDeltaAsync(
            fixture.CreateInventoryDelta(2, [new InventorySlotUpdate(1, 0, new InventorySlotValue(100, 2, false))]));
        var reconciled = fixture.CreatePlayerInventoryObservation(3,
        [
            new InventoryItemObservation(1, 0, 100, 2, false),
            new InventoryItemObservation(2, 4, 400, 5, true),
        ]);
        await fixture.Store.WriteAsync(reconciled);

        var changes = await fixture.Store.ReadInventoryChangesAsync(baseline.Scope.Owner, first.CurrentRevision!.Value);
        var read = await fixture.Store.ReadCurrentAsync(baseline.Scope);
        var payload = read.Observation!.Payload.Deserialize<InventoryObservationPayload>(ObservationPayloadContracts.PlayerInventory, 1);

        Assert.Equal(InventoryChangeReadStatus.SnapshotRequired, changes.Status);
        Assert.Equal([(1, 0, 100U, 2), (2, 4, 400U, 5)], payload.Items.Select(item => (item.ContainerId, item.SlotIndex, item.ItemId, item.Quantity)));
    }

    [Fact]
    public async Task Failed_inventory_slot_update_rolls_back_projection_and_change_feed()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var baseline = fixture.CreatePlayerInventoryObservation(1, [new InventoryItemObservation(1, 0, 100, 4, false)]);
        var first = await fixture.Store.WriteAsync(baseline);
        await using (var connection = new SqliteConnection($"Data Source={fixture.DatabasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TRIGGER reject_inventory_slot_update
                BEFORE UPDATE ON inventory_slot_projection
                BEGIN
                    SELECT RAISE(ABORT, 'synthetic slot failure');
                END;
                """;
            await command.ExecuteNonQueryAsync();
        }

        var write = await fixture.Store.WriteInventoryDeltaAsync(
            fixture.CreateInventoryDelta(2, [new InventorySlotUpdate(1, 0, new InventorySlotValue(100, 9, false))]));
        var current = await fixture.Store.ReadCurrentAsync(baseline.Scope);
        var changes = await fixture.Store.ReadInventoryChangesAsync(baseline.Scope.Owner, first.CurrentRevision!.Value);

        Assert.Equal(ObservationWriteStatus.Unavailable, write.Status);
        Assert.Equal(4, Assert.Single(current.Observation!.Payload.Deserialize<InventoryObservationPayload>(ObservationPayloadContracts.PlayerInventory, 1).Items).Quantity);
        Assert.Equal(InventoryChangeReadStatus.NoChanges, changes.Status);
    }

    [Fact]
    public async Task Complete_empty_snapshot_atomically_replaces_prior_listings()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var first = fixture.CreateListingObservation(1, [new(300, 400, 2, 50, false)]);
        Assert.Equal(ObservationWriteStatus.AcceptedChanged, (await fixture.Store.WriteAsync(first)).Status);

        var empty = fixture.CreateListingObservation(2, []);
        Assert.Equal(ObservationWriteStatus.AcceptedChanged, (await fixture.Store.WriteAsync(empty)).Status);

        var read = await fixture.Store.ReadCurrentAsync(empty.Scope);
        Assert.Equal(ObservationReadStatus.Found, read.Status);
        Assert.Empty(read.Observation!.Payload.Deserialize<RetainerMarketListingsPayload>(
            ObservationPayloadContracts.RetainerMarketListings,
            ObservationPayloadContracts.Version).Listings);
        Assert.False(read.Observation.IsStale);
    }

    [Fact]
    public async Task Unavailable_newer_capture_preserves_payload_and_marks_it_stale()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var first = fixture.CreateListingObservation(1, [new(300, 400, 2, 50, false)]);
        await fixture.Store.WriteAsync(first);
        var unavailable = fixture.CreateListingObservation(2, null, ObservationEvidence.CompleteAvailable with
        {
            Availability = ObservationAvailability.Transitioning,
            ContainerLoaded = false,
        });

        var write = await fixture.Store.WriteAsync(unavailable);
        var read = await fixture.Store.ReadCurrentAsync(first.Scope);

        Assert.Equal(ObservationWriteStatus.PreservedAsStale, write.Status);
        Assert.True(read.Observation!.IsStale);
        Assert.Equal(1, read.Observation.Capture.SourceRevision);
        Assert.Single(read.Observation.Payload.Deserialize<RetainerMarketListingsPayload>(
            ObservationPayloadContracts.RetainerMarketListings,
            1).Listings);
    }

    [Fact]
    public async Task Stale_capture_advances_source_order_without_replacing_payload_provenance()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var first = fixture.CreateListingObservation(1, [new(300, 400, 2, 50, false)]);
        await fixture.Store.WriteAsync(first);
        var unavailable = fixture.CreateListingObservation(3, null, ObservationEvidence.CompleteAvailable with
        {
            Availability = ObservationAvailability.Transitioning,
            ContainerLoaded = false,
        });

        var stale = await fixture.Store.WriteAsync(unavailable);
        var older = await fixture.Store.WriteAsync(fixture.CreateListingObservation(2, []));
        var repeated = await fixture.Store.WriteAsync(unavailable);
        var read = await fixture.Store.ReadCurrentAsync(first.Scope);

        Assert.Equal(ObservationWriteStatus.PreservedAsStale, stale.Status);
        Assert.Equal(ObservationWriteStatus.IgnoredOlderRevision, older.Status);
        Assert.Equal(ObservationWriteStatus.IgnoredRepeatedRevision, repeated.Status);
        Assert.True(read.Observation!.IsStale);
        Assert.Equal(1, read.Observation.Capture.SourceRevision);
        Assert.Single(read.Observation.Payload.Deserialize<RetainerMarketListingsPayload>(
            ObservationPayloadContracts.RetainerMarketListings,
            1).Listings);
    }

    [Fact]
    public async Task Identical_newer_payload_advances_confirmation_without_duplicate_full_payload()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var rows = new[] { new RetainerMarketListingObservation(300, 400, 2, 50, false) };
        var first = fixture.CreateListingObservation(1, rows);
        var confirmation = fixture.CreateListingObservation(2, rows);

        await fixture.Store.WriteAsync(first);
        var write = await fixture.Store.WriteAsync(confirmation);
        var read = await fixture.Store.ReadCurrentAsync(first.Scope);

        Assert.Equal(ObservationWriteStatus.AcceptedConfirmed, write.Status);
        Assert.Equal(2, read.Observation!.Capture.SourceRevision);
        Assert.Equal(2, read.Observation.ConfirmationCount);
        Assert.Equal(1, await fixture.Store.CountHistoryPayloadsAsync(first.Scope));
    }

    [Fact]
    public async Task Repeated_and_older_revisions_change_nothing()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var current = fixture.CreateListingObservation(2, [new(300, 400, 2, 50, false)]);
        await fixture.Store.WriteAsync(current);

        var repeated = await fixture.Store.WriteAsync(fixture.CreateListingObservation(2, []));
        var older = await fixture.Store.WriteAsync(fixture.CreateListingObservation(1, []));
        var read = await fixture.Store.ReadCurrentAsync(current.Scope);

        Assert.Equal(ObservationWriteStatus.IgnoredRepeatedRevision, repeated.Status);
        Assert.Equal(ObservationWriteStatus.IgnoredOlderRevision, older.Status);
        Assert.Single(read.Observation!.Payload.Deserialize<RetainerMarketListingsPayload>(
            ObservationPayloadContracts.RetainerMarketListings,
            1).Listings);
    }

    [Fact]
    public async Task Owner_mismatch_never_creates_current_state()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var invalid = fixture.CreateListingObservation(1, []);
        invalid = invalid with
        {
            Scope = invalid.Scope with
            {
                Subject = invalid.Scope.Subject with { OwnerLocalContentId = 999 },
            },
        };

        var write = await fixture.Store.WriteAsync(invalid);
        var read = await fixture.Store.ReadCurrentAsync(invalid.Scope);

        Assert.Equal(ObservationWriteStatus.Rejected, write.Status);
        Assert.Equal(ObservationReadStatus.NotObserved, read.Status);
    }

    [Fact]
    public async Task Failed_projection_update_rolls_back_history_and_preserves_current_snapshot()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var first = fixture.CreateListingObservation(1, [new(300, 400, 2, 50, false)]);
        await fixture.Store.WriteAsync(first);
        await using (var connection = new SqliteConnection($"Data Source={fixture.DatabasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TRIGGER reject_projection_update
                BEFORE UPDATE ON current_projection
                BEGIN
                    SELECT RAISE(ABORT, 'synthetic projection failure');
                END;
                """;
            await command.ExecuteNonQueryAsync();
        }

        var write = await fixture.Store.WriteAsync(fixture.CreateListingObservation(2, []));
        var read = await fixture.Store.ReadCurrentAsync(first.Scope);

        Assert.Equal(ObservationWriteStatus.Unavailable, write.Status);
        Assert.Equal(1, read.Observation!.Capture.SourceRevision);
        Assert.Single(read.Observation.Payload.Deserialize<RetainerMarketListingsPayload>(
            ObservationPayloadContracts.RetainerMarketListings,
            1).Listings);
        Assert.Equal(1, await fixture.Store.CountHistoryPayloadsAsync(first.Scope));
    }

    [Fact]
    public async Task Unsupported_schema_returns_explicit_open_result()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(root, "observations.db");
            await using (var connection = new SqliteConnection($"Data Source={path}"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA user_version = 2000;";
                await command.ExecuteNonQueryAsync();
            }

            var result = await SqliteObservationStore.OpenAsync(new ObservationStoreOptions { DatabasePath = path });

            Assert.Equal(ObservationStoreOpenStatus.UnsupportedDatabaseVersion, result.Status);
            Assert.Null(result.Store);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task New_plugin_instance_after_reopen_receives_next_durable_revision()
    {
        var root = CreateTemporaryDirectory();
        var path = Path.Combine(root, "observations.db");
        try
        {
            var firstOpen = await SqliteObservationStore.OpenAsync(new ObservationStoreOptions { DatabasePath = path });
            Assert.True(firstOpen.IsReady, firstOpen.Message);
            var owner = new ObservationOwner(100, 74);
            var scope = new ObservationScope(owner, ObservationSubject.Retainer(200, owner), ObservationContainerKind.RetainerMarketListings);
            var first = ObservationForInstance(scope, "first-load", 8, new DateTimeOffset(2026, 7, 31, 12, 0, 0, TimeSpan.Zero));
            Assert.Equal(1, (await firstOpen.Store!.WriteAsync(first)).CurrentRevision);
            await firstOpen.Store.DisposeAsync();

            var secondOpen = await SqliteObservationStore.OpenAsync(new ObservationStoreOptions { DatabasePath = path });
            Assert.True(secondOpen.IsReady, secondOpen.Message);
            var second = ObservationForInstance(scope, "second-load", 1, new DateTimeOffset(2026, 7, 31, 12, 1, 0, TimeSpan.Zero));
            var write = await secondOpen.Store!.WriteAsync(second);
            var read = await secondOpen.Store.ReadCurrentAsync(scope);

            Assert.Equal(ObservationWriteStatus.AcceptedConfirmed, write.Status);
            Assert.Equal(2, write.CurrentRevision);
            Assert.Equal(2, read.Observation!.Revision);
            Assert.Equal(1, read.Observation.Capture.SourceRevision);
            await secondOpen.Store.DisposeAsync();
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Throwing_change_subscriber_cannot_turn_committed_write_into_failure()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        fixture.Store.Changed += (_, _) => throw new InvalidOperationException("synthetic subscriber failure");

        var write = await fixture.Store.WriteAsync(fixture.CreateListingObservation(1, []));
        var read = await fixture.Store.ReadCurrentAsync(fixture.CreateListingObservation(1, []).Scope);

        Assert.Equal(ObservationWriteStatus.AcceptedChanged, write.Status);
        Assert.Equal(ObservationReadStatus.Found, read.Status);
        Assert.Contains("synthetic subscriber failure", fixture.Store.LastNotificationError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Corrupt_database_is_quarantined_instead_of_overwritten()
    {
        var root = CreateTemporaryDirectory();
        var path = Path.Combine(root, "observations.db");
        await File.WriteAllTextAsync(path, "not a sqlite database");
        try
        {
            var open = await SqliteObservationStore.OpenAsync(new ObservationStoreOptions { DatabasePath = path });

            Assert.Equal(ObservationStoreOpenStatus.CorruptDatabase, open.Status);
            Assert.False(File.Exists(path));
            Assert.Single(Directory.EnumerateFiles(Path.Combine(root, "quarantine")));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Reader_probe_does_not_create_a_missing_database()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(root, "observations.db");
            var probe = await ObservationDatabaseProbe.ReadAsync(new ObservationStoreOptions { DatabasePath = path });

            Assert.Equal(ObservationDatabaseProbeStatus.Missing, probe.Status);
            Assert.False(File.Exists(path));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Empty_database_left_by_an_interrupted_open_is_recreated()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(root, "observations.db");
            await File.WriteAllBytesAsync(path, []);

            var probe = await ObservationDatabaseProbe.ReadAsync(new ObservationStoreOptions { DatabasePath = path });
            var open = await SqliteObservationStore.OpenAsync(new ObservationStoreOptions { DatabasePath = path });
            Assert.True(open.IsReady, open.Message);
            await open.Store!.DisposeAsync();
            var repaired = await ObservationDatabaseProbe.ReadAsync(new ObservationStoreOptions { DatabasePath = path });

            Assert.Equal(ObservationDatabaseProbeStatus.Missing, probe.Status);
            Assert.Equal(ObservationDatabaseProbeStatus.Compatible, repaired.Status);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Read_only_open_never_creates_or_migrates_state()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var missingPath = Path.Combine(root, "missing.db");
            var missing = await SqliteObservationReader.OpenAsync(new ObservationStoreOptions { DatabasePath = missingPath });
            Assert.Equal(ObservationStoreOpenStatus.Missing, missing.Status);
            Assert.False(File.Exists(missingPath));

            var legacyPath = Path.Combine(root, "legacy.db");
            await CreateLegacyVersion10DatabaseAsync(legacyPath);
            var legacy = await SqliteObservationReader.OpenAsync(new ObservationStoreOptions { DatabasePath = legacyPath });
            Assert.Equal(ObservationStoreOpenStatus.UpgradeRequired, legacy.Status);
            Assert.Null(legacy.Reader);
            var probe = await ObservationDatabaseProbe.ReadAsync(new ObservationStoreOptions { DatabasePath = legacyPath });
            Assert.Equal(ObservationDatabaseProbeStatus.UpgradeRequired, probe.Status);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Version_1_0_database_migrates_forward_after_consistent_backup()
    {
        var root = CreateTemporaryDirectory();
        var path = Path.Combine(root, "observations.db");
        try
        {
            await CreateLegacyVersion10DatabaseAsync(path);
            var probe = await ObservationDatabaseProbe.ReadAsync(new ObservationStoreOptions { DatabasePath = path });
            Assert.Equal(ObservationDatabaseProbeStatus.UpgradeRequired, probe.Status);

            var open = await SqliteObservationStore.OpenAsync(new ObservationStoreOptions { DatabasePath = path });
            Assert.True(open.IsReady, open.Message);
            await open.Store!.DisposeAsync();

            var migrated = await ObservationDatabaseProbe.ReadAsync(new ObservationStoreOptions { DatabasePath = path });
            Assert.Equal(ObservationDatabaseProbeStatus.Compatible, migrated.Status);
            Assert.Equal(new ObservationVersion(1, 2), migrated.SchemaVersion);
            Assert.Equal(2, Directory.EnumerateFiles(Path.Combine(root, "backups"), "*.db").Count());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Version_1_1_inventory_payload_migrates_to_normalized_slots()
    {
        var root = CreateTemporaryDirectory();
        var path = Path.Combine(root, "observations.db");
        try
        {
            var created = await SqliteObservationStore.OpenAsync(new ObservationStoreOptions { DatabasePath = path });
            Assert.True(created.IsReady, created.Message);
            var owner = new ObservationOwner(100, 74);
            var scope = new ObservationScope(owner, ObservationSubject.Character(owner), ObservationContainerKind.PlayerInventory);
            var baseline = new ObservationEnvelope(
                scope,
                new ObservationCapture(1, new DateTimeOffset(2026, 7, 31, 12, 0, 0, TimeSpan.Zero),
                    new ObservationProvenance("TestPlugin", "legacy", "1.0.0", "2026.07.31.0000.0000"), ObservationEvidence.CompleteAvailable),
                ObservationPayload.Create(ObservationPayloadContracts.PlayerInventory, 1,
                    new InventoryObservationPayload([1], [1], [new InventoryItemObservation(1, 2, 500, 6, true)])));
            await created.Store!.WriteAsync(baseline);
            await created.Store.DisposeAsync();
            await DowngradeToVersion11Async(path);

            var before = await ObservationDatabaseProbe.ReadAsync(new ObservationStoreOptions { DatabasePath = path });
            Assert.Equal(ObservationDatabaseProbeStatus.UpgradeRequired, before.Status);
            var migrated = await SqliteObservationStore.OpenAsync(new ObservationStoreOptions { DatabasePath = path });
            Assert.True(migrated.IsReady, migrated.Message);
            var read = await migrated.Store!.ReadCurrentAsync(scope);
            var item = Assert.Single(read.Observation!.Payload.Deserialize<InventoryObservationPayload>(ObservationPayloadContracts.PlayerInventory, 1).Items);
            var delta = await migrated.Store.WriteInventoryDeltaAsync(new InventoryObservationDelta(
                scope,
                baseline.Capture with { SourceRevision = 2, ObservedAtUtc = baseline.Capture.ObservedAtUtc.AddMinutes(1) },
                [new InventorySlotUpdate(1, 2, new InventorySlotValue(500, 3, true))]));

            Assert.Equal(6, item.Quantity);
            Assert.True(delta.Status == ObservationWriteStatus.AcceptedChanged, delta.Message);
            Assert.Single(Directory.EnumerateFiles(Path.Combine(root, "backups"), "*.db"));
            await migrated.Store.DisposeAsync();
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Writer_capability_one_cannot_start_the_version_1_1_migration()
    {
        var root = CreateTemporaryDirectory();
        var path = Path.Combine(root, "observations.db");
        try
        {
            var created = await SqliteObservationStore.OpenAsync(new ObservationStoreOptions { DatabasePath = path });
            Assert.True(created.IsReady, created.Message);
            await created.Store!.DisposeAsync();
            await DowngradeToVersion11Async(path);

            var open = await SqliteObservationStore.OpenAsync(new ObservationStoreOptions
            {
                DatabasePath = path,
                WriterCapability = 1,
            });

            Assert.Equal(ObservationStoreOpenStatus.IncompatibleWriterCapability, open.Status);
            Assert.False(Directory.Exists(Path.Combine(root, "backups")));
            var probe = await ObservationDatabaseProbe.ReadAsync(new ObservationStoreOptions { DatabasePath = path });
            Assert.Equal(ObservationDatabaseProbeStatus.UpgradeRequired, probe.Status);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Failed_migration_preserves_version_1_0_database_and_backup()
    {
        var root = CreateTemporaryDirectory();
        var path = Path.Combine(root, "observations.db");
        try
        {
            await CreateLegacyVersion10DatabaseAsync(path);
            var open = await SqliteObservationStore.OpenAsync(new ObservationStoreOptions
            {
                DatabasePath = path,
                BeforeMigrationCommit = () => throw new InvalidOperationException("synthetic migration failure"),
            });

            Assert.Equal(ObservationStoreOpenStatus.Unavailable, open.Status);
            var probe = await ObservationDatabaseProbe.ReadAsync(new ObservationStoreOptions { DatabasePath = path });
            Assert.Equal(ObservationDatabaseProbeStatus.UpgradeRequired, probe.Status);
            Assert.Single(Directory.EnumerateFiles(Path.Combine(root, "backups"), "*.db"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Writer_below_persisted_capability_cannot_open_or_migrate()
    {
        var root = CreateTemporaryDirectory();
        var path = Path.Combine(root, "observations.db");
        try
        {
            await CreateLegacyVersion10DatabaseAsync(path);
            await using (var connection = new SqliteConnection($"Data Source={path}"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "UPDATE observation_metadata SET value = '2' WHERE key = 'minimum_writer_capability';";
                await command.ExecuteNonQueryAsync();
            }

            var open = await SqliteObservationStore.OpenAsync(new ObservationStoreOptions
            {
                DatabasePath = path,
                WriterCapability = 1,
            });

            Assert.Equal(ObservationStoreOpenStatus.IncompatibleWriterCapability, open.Status);
            Assert.False(Directory.Exists(Path.Combine(root, "backups")));
            var probe = await ObservationDatabaseProbe.ReadAsync(new ObservationStoreOptions { DatabasePath = path });
            Assert.Equal(ObservationDatabaseProbeStatus.UpgradeRequired, probe.Status);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Committed_write_wakes_a_separate_change_monitor()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        await using var monitor = new ObservationDatabaseChangeMonitor(new ObservationStoreOptions
        {
            DatabasePath = fixture.DatabasePath,
        });
        await using var secondMonitor = new ObservationDatabaseChangeMonitor(new ObservationStoreOptions
        {
            DatabasePath = fixture.DatabasePath,
        });
        var changed = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondChanged = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        monitor.Changed += (_, change) => changed.TrySetResult(change.Revision);
        secondMonitor.Changed += (_, change) => secondChanged.TrySetResult(change.Revision);
        await monitor.StartAsync();
        await secondMonitor.StartAsync();

        var write = await fixture.Store.WriteAsync(fixture.CreateListingObservation(1, []));

        Assert.Equal(write.CurrentRevision, await changed.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(write.CurrentRevision, await secondChanged.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(write.CurrentRevision, monitor.LastRevision);
    }

    [Fact]
    public async Task Change_monitor_rearms_immediately_after_watcher_failure()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        await using var monitor = new ObservationDatabaseChangeMonitor(new ObservationStoreOptions
        {
            DatabasePath = fixture.DatabasePath,
        });
        var changed = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        monitor.Changed += (_, change) => changed.TrySetResult(change.Revision);
        await monitor.StartAsync();

        monitor.ReportWatcherError(new InternalBufferOverflowException("synthetic watcher failure"));
        var write = await fixture.Store.WriteAsync(fixture.CreateListingObservation(1, []));

        Assert.Null(monitor.LastNotificationError);
        Assert.Equal(write.CurrentRevision, await changed.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Owner_container_query_returns_only_matching_retainer_listings()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var first = fixture.CreateListingObservation(1, [new(0, 100, 1, 10, false)]);
        var second = fixture.CreateListingObservation(2, [new(0, 200, 1, 20, false)]) with
        {
            Scope = first.Scope with { Subject = ObservationSubject.Retainer(201, first.Scope.Owner) },
        };
        var otherOwner = new ObservationOwner(999, 74);
        var other = fixture.CreateListingObservation(3, []) with
        {
            Scope = new ObservationScope(otherOwner, ObservationSubject.Retainer(300, otherOwner), ObservationContainerKind.RetainerMarketListings),
        };
        await fixture.Store.WriteAsync(first);
        await fixture.Store.WriteAsync(second);
        await fixture.Store.WriteAsync(other);

        var read = await fixture.Store.ReadCurrentByOwnerAsync(first.Scope.Owner, ObservationContainerKind.RetainerMarketListings);

        Assert.Equal(ObservationReadStatus.Found, read.Status);
        Assert.Equal([200UL, 201UL], read.Observations.Select(observation => observation.Scope.Subject.Id));
    }

    private static async Task CreateLegacyVersion10DatabaseAsync(string path)
    {
        var open = await SqliteObservationStore.OpenAsync(new ObservationStoreOptions { DatabasePath = path });
        Assert.True(open.IsReady, open.Message);
        await open.Store!.DisposeAsync();
        SqliteConnection.ClearAllPools();
        await using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DROP INDEX ix_inventory_slot_change_scope_revision;
            DROP TABLE inventory_slot_change;
            DROP INDEX ix_inventory_slot_projection_scope_item;
            DROP TABLE inventory_slot_projection;
            DROP TABLE inventory_scope_projection;
            DROP INDEX ix_current_projection_owner_container;
            ALTER TABLE current_projection DROP COLUMN owner_local_content_id;
            ALTER TABLE current_projection DROP COLUMN owner_home_world_id;
            ALTER TABLE current_projection DROP COLUMN container_kind;
            ALTER TABLE current_projection DROP COLUMN source_order_capture_json;
            DELETE FROM observation_metadata WHERE key = 'change_revision';
            UPDATE observation_metadata SET value = '0' WHERE key = 'schema_minor';
            PRAGMA user_version = 1000;
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task DowngradeToVersion11Async(string path)
    {
        SqliteConnection.ClearAllPools();
        await using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DROP INDEX ix_inventory_slot_change_scope_revision;
            DROP TABLE inventory_slot_change;
            DROP INDEX ix_inventory_slot_projection_scope_item;
            DROP TABLE inventory_slot_projection;
            DROP TABLE inventory_scope_projection;
            UPDATE observation_metadata SET value = '1' WHERE key = 'schema_minor';
            UPDATE observation_metadata SET value = '1' WHERE key = 'minimum_writer_capability';
            PRAGMA user_version = 1001;
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static ObservationEnvelope ObservationForInstance(
        ObservationScope scope,
        string instance,
        long sourceRevision,
        DateTimeOffset observedAtUtc) =>
        new(
            scope,
            new ObservationCapture(
                sourceRevision,
                observedAtUtc,
                new ObservationProvenance("TestPlugin", instance, "1.0.0", "2026.07.31.0000.0000"),
                ObservationEvidence.CompleteAvailable),
            ObservationPayload.Create(
                ObservationPayloadContracts.RetainerMarketListings,
                1,
                new RetainerMarketListingsPayload([])));

    private sealed class StoreFixture : IAsyncDisposable
    {
        private readonly string root;

        private StoreFixture(string root, string databasePath, SqliteObservationStore store)
        {
            this.root = root;
            DatabasePath = databasePath;
            Store = store;
        }

        public string DatabasePath { get; }
        public SqliteObservationStore Store { get; }

        public static async ValueTask<StoreFixture> CreateAsync()
        {
            var root = CreateTemporaryDirectory();
            var path = Path.Combine(root, "observations.db");
            var result = await SqliteObservationStore.OpenAsync(new ObservationStoreOptions { DatabasePath = path });
            Assert.True(result.IsReady, result.Message);
            return new StoreFixture(root, path, result.Store!);
        }

        public ObservationEnvelope CreatePlayerInventoryObservation(
            long revision,
            IReadOnlyList<InventoryItemObservation> items)
        {
            var owner = new ObservationOwner(100, 74);
            var containers = new[] { 1, 2 };
            return new ObservationEnvelope(
                new ObservationScope(owner, ObservationSubject.Character(owner), ObservationContainerKind.PlayerInventory),
                Capture(revision),
                ObservationPayload.Create(
                    ObservationPayloadContracts.PlayerInventory,
                    ObservationPayloadContracts.Version,
                    new InventoryObservationPayload(containers, containers, items)));
        }

        public InventoryObservationDelta CreateInventoryDelta(
            long revision,
            IReadOnlyList<InventorySlotUpdate> updates)
        {
            var owner = new ObservationOwner(100, 74);
            return new InventoryObservationDelta(
                new ObservationScope(owner, ObservationSubject.Character(owner), ObservationContainerKind.PlayerInventory),
                Capture(revision),
                updates);
        }

        public ObservationEnvelope CreateListingObservation(
            long revision,
            IReadOnlyList<RetainerMarketListingObservation>? rows,
            ObservationEvidence? evidence = null)
        {
            var owner = new ObservationOwner(100, 74);
            return new ObservationEnvelope(
                new ObservationScope(
                    owner,
                    ObservationSubject.Retainer(200, owner),
                    ObservationContainerKind.RetainerMarketListings),
                new ObservationCapture(
                    revision,
                    new DateTimeOffset(2026, 7, 31, 12, 0, 0, TimeSpan.Zero).AddMinutes(revision),
                    new ObservationProvenance("TestPlugin", "instance", "1.0.0", "2026.07.31.0000.0000"),
                    evidence ?? ObservationEvidence.CompleteAvailable),
                rows is null
                    ? null
                    : ObservationPayload.Create(
                        ObservationPayloadContracts.RetainerMarketListings,
                        ObservationPayloadContracts.Version,
                        new RetainerMarketListingsPayload(rows)));
        }

        private static ObservationCapture Capture(long revision) => new(
            revision,
            new DateTimeOffset(2026, 7, 31, 12, 0, 0, TimeSpan.Zero).AddMinutes(revision),
            new ObservationProvenance("TestPlugin", "instance", "1.0.0", "2026.07.31.0000.0000"),
            ObservationEvidence.CompleteAvailable);

        public async ValueTask DisposeAsync()
        {
            await Store.DisposeAsync();
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "Franthropy.Observations.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
