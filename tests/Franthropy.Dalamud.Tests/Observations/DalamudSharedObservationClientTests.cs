using Franthropy.Dalamud.Observations;
using Franthropy.Observations.Storage;
using Franthropy.Observations.V1;
using Microsoft.Data.Sqlite;

namespace Franthropy.Dalamud.Tests.Observations;

public sealed class DalamudSharedObservationClientTests
{
    [Fact]
    public async Task ReadsCurrentOwnerRetainersWithoutHostingCollector()
    {
        var root = Path.Combine(Path.GetTempPath(), nameof(DalamudSharedObservationClientTests), Guid.NewGuid().ToString("N"));
        var pluginDirectory = Path.Combine(root, "pluginConfigs", "MarketMafioso");
        Directory.CreateDirectory(pluginDirectory);
        var paths = SharedObservationPaths.FromPluginConfigDirectory(pluginDirectory);
        var storeOptions = new ObservationStoreOptions
        {
            DatabasePath = paths.DatabasePath,
            BackupDirectory = paths.BackupsDirectory,
            MigrationLockPath = paths.MigrationLockPath,
            ChangeSignalPath = paths.ChangeSignalPath,
            WriterCapability = 2,
        };
        var open = await SqliteObservationStore.OpenAsync(storeOptions);
        Assert.True(open.IsReady, open.Message);
        var store = open.Store!;
        var owner = new ObservationOwner(100, 74);
        var otherOwner = new ObservationOwner(101, 74);
        await store.WriteAsync(CreateRoster(owner, 1, 200, "Alpha"));
        await store.WriteAsync(CreateInventory(owner, 2, 200, 5333));
        await store.WriteAsync(CreateRoster(otherOwner, 1, 201, "Other"));

        var changed = new TaskCompletionSource<SharedRetainerObservationSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new DalamudSharedObservationClient(new DalamudSharedObservationClientOptions
        {
            PluginConfigDirectory = pluginDirectory,
            CurrentOwner = () => owner,
        });
        client.RetainersChanged += (_, snapshot) => changed.TrySetResult(snapshot);
        client.Start();

        var result = await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(owner, result.Owner);
        Assert.Equal(2, result.Observations.Length);
        Assert.All(result.Observations, observation => Assert.Equal(owner, observation.Scope.Owner));
        Assert.True(client.TryGetRetainers(owner, out var current));
        Assert.Equal(result, current);
        Assert.False(client.TryGetRetainers(otherOwner, out _));

        await client.DisposeAsync();
        await store.DisposeAsync();
        SqliteConnection.ClearAllPools();
        Directory.Delete(root, recursive: true);
    }

    private static ObservationEnvelope CreateRoster(
        ObservationOwner owner,
        long revision,
        ulong retainerId,
        string name) =>
        new(
            new ObservationScope(owner, ObservationSubject.Character(owner), ObservationContainerKind.RetainerRoster),
            Capture(revision),
            ObservationPayload.Create(
                ObservationPayloadContracts.RetainerRoster,
                ObservationPayloadContracts.Version,
                new RetainerRosterPayload([new RetainerRosterObservation(retainerId, name, owner.HomeWorldId)])));

    private static ObservationEnvelope CreateInventory(
        ObservationOwner owner,
        long revision,
        ulong retainerId,
        uint itemId) =>
        new(
            new ObservationScope(owner, ObservationSubject.Retainer(retainerId, owner), ObservationContainerKind.RetainerInventory),
            Capture(revision),
            ObservationPayload.Create(
                ObservationPayloadContracts.RetainerInventory,
                ObservationPayloadContracts.Version,
                new InventoryObservationPayload([10000], [10000], [new InventoryItemObservation(10000, 0, itemId, 3, false)])));

    private static ObservationCapture Capture(long revision) => new(
        revision,
        DateTimeOffset.UtcNow.AddSeconds(revision),
        new ObservationProvenance("Test", "instance", "1.0.0", "test-build"),
        ObservationEvidence.CompleteAvailable);
}
