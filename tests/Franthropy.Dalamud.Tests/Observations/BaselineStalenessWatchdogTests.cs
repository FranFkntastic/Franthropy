using Franthropy.Dalamud.Observations.Reporting;
using Franthropy.Observations.Storage;
using Franthropy.Observations.V1;

namespace Franthropy.Dalamud.Tests.Observations;

public sealed class BaselineStalenessWatchdogTests
{
    [Fact]
    public async Task Watchdog_finds_real_player_key_and_does_not_label_player_staleness_as_retainer()
    {
        var directory = Path.Combine(Path.GetTempPath(), "observation-watchdog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var options = new ObservationStoreOptions { DatabasePath = Path.Combine(directory, "observations.db") };
            var opened = await SqliteObservationStore.OpenAsync(options);
            Assert.True(opened.IsReady, opened.Message);
            await using var store = opened.Store!;
            var owner = new ObservationOwner(123, 74);
            var scope = new ObservationScope(owner, ObservationSubject.Character(owner), ObservationContainerKind.PlayerInventory);
            Assert.Equal("000000000000007B:74:0:000000000000007B:0", SqliteObservationStore.CreateScopeKey(scope));
            var envelope = new ObservationEnvelope(scope,
                new ObservationCapture(1, DateTimeOffset.UtcNow, new ObservationProvenance("test", "instance", "1", "test"), ObservationEvidence.CompleteAvailable),
                ObservationPayload.Create(ObservationPayloadContracts.PlayerInventory, ObservationPayloadContracts.Version,
                    new InventoryObservationPayload([1], [1], [])));
            var result = await store.WriteAsync(envelope);
            Assert.Equal(ObservationWriteStatus.AcceptedChanged, result.Status);
            var messages = new List<string>();
            using var watchdog = new BaselineStalenessWatchdog(options, new()
            {
                CurrentContentId = () => owner.LocalContentId,
                CurrentHomeWorldId = () => owner.HomeWorldId,
            }, messages.Add);
            await watchdog.CheckOnceAsync(CancellationToken.None);
            Assert.Empty(messages);
            await store.InvalidateAsync(scope, "test stale", DateTimeOffset.UtcNow);
            await watchdog.CheckOnceAsync(CancellationToken.None);
            Assert.Single(messages);
            Assert.Contains("Inventory baseline is STALE", messages[0]);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }
}
