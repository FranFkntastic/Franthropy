using Franthropy.Observations.Hosting;
using Franthropy.Observations.Storage;
using Franthropy.Observations.V1;

namespace Franthropy.Observations.Tests.Hosting;

public sealed class ObservationCollectorCoordinatorTests
{
    [Fact]
    public async Task Better_candidate_takes_over_and_unload_returns_ownership()
    {
        using var fixture = new CoordinatorFixture();
        var lowStarts = 0;
        var lowStops = 0;
        using var low = fixture.Create("Low", "low", new Version(1, 0), 1, () => lowStarts++, () => lowStops++);
        low.Start();
        await WaitForStateAsync(low, ObservationLeadershipState.Collector);

        using var high = fixture.Create("High", "high", new Version(2, 0), 2);
        high.Start();
        await WaitForStateAsync(high, ObservationLeadershipState.Collector);
        await WaitForStateAsync(low, ObservationLeadershipState.Reader);

        high.Dispose();
        await WaitForStateAsync(low, ObservationLeadershipState.Collector);

        Assert.Equal(2, lowStarts);
        Assert.Equal(1, lowStops);
    }

    [Fact]
    public async Task Collector_fault_relinquishes_to_next_candidate_without_a_heartbeat()
    {
        using var fixture = new CoordinatorFixture();
        using var leader = fixture.Create("Leader", "leader", new Version(2, 0), 2);
        using var follower = fixture.Create("Follower", "follower", new Version(1, 0), 1);
        leader.Start();
        follower.Start();
        await WaitForStateAsync(leader, ObservationLeadershipState.Collector);

        leader.ReportCollectorFault("synthetic collector failure");

        await WaitForStateAsync(leader, ObservationLeadershipState.Faulted);
        await WaitForStateAsync(follower, ObservationLeadershipState.Collector);
    }

    [Fact]
    public async Task Quiet_inputs_produce_no_election_or_candidate_writes()
    {
        using var fixture = new CoordinatorFixture();
        using var coordinator = fixture.Create("Only", "only", new Version(1, 0), 1);
        var eventCount = 0;
        coordinator.LeadershipChanged += (_, _) => Interlocked.Increment(ref eventCount);
        coordinator.Start();
        await WaitForStateAsync(coordinator, ObservationLeadershipState.Collector);
        var countAtIdle = Volatile.Read(ref eventCount);
        var writeAtIdle = File.GetLastWriteTimeUtc(coordinator.CandidatePath);

        await Task.Delay(350);

        Assert.Equal(countAtIdle, Volatile.Read(ref eventCount));
        Assert.Equal(writeAtIdle, File.GetLastWriteTimeUtc(coordinator.CandidatePath));
        Assert.Equal(ObservationLeadershipState.Collector, coordinator.State.State);
    }

    [Fact]
    public async Task Abandoned_candidate_file_is_removed_during_load_event()
    {
        using var fixture = new CoordinatorFixture();
        var abandoned = Path.Combine(fixture.CandidatesDirectory, "abandoned.json");
        File.WriteAllText(abandoned, "{}");
        using var coordinator = fixture.Create("Only", "only", new Version(1, 0), 1);

        coordinator.Start();
        await WaitForStateAsync(coordinator, ObservationLeadershipState.Collector);

        Assert.False(File.Exists(abandoned));
    }

    [Fact]
    public async Task Persisted_minimum_writer_capability_excludes_an_older_host()
    {
        using var fixture = new CoordinatorFixture();
        static ObservationDatabaseProbeResult Probe() => new(
            ObservationDatabaseProbeStatus.Compatible,
            new ObservationVersion(1, 1),
            new ObservationVersion(1, 0),
            MinimumWriterCapability: 2,
            CurrentRevision: 10,
            Message: "compatible");
        using var old = fixture.Create("Old", "old", new Version(1, 0), 1, databaseProbe: Probe);
        using var current = fixture.Create("Current", "current", new Version(2, 0), 2, databaseProbe: Probe);

        old.Start();
        await WaitForStateAsync(old, ObservationLeadershipState.Reader);
        current.Start();
        await WaitForStateAsync(current, ObservationLeadershipState.Collector);

        Assert.NotEqual(ObservationLeadershipState.Collector, old.State.State);
    }

    private static async Task WaitForStateAsync(
        ObservationCollectorCoordinator coordinator,
        ObservationLeadershipState expected)
    {
        if (coordinator.State.State == expected)
            return;

        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(object? sender, ObservationLeadershipSnapshot snapshot)
        {
            if (snapshot.State == expected)
                reached.TrySetResult();
        }

        coordinator.LeadershipChanged += Handler;
        try
        {
            if (coordinator.State.State == expected)
                return;
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            coordinator.LeadershipChanged -= Handler;
        }
    }

    private sealed class CoordinatorFixture : IDisposable
    {
        public CoordinatorFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "Franthropy.Coordinator.Tests", Guid.NewGuid().ToString("N"));
            CandidatesDirectory = Path.Combine(Root, "candidates");
            Directory.CreateDirectory(CandidatesDirectory);
        }

        public string Root { get; }
        public string CandidatesDirectory { get; }

        public ObservationCollectorCoordinator Create(
            string pluginName,
            string instanceId,
            Version version,
            int capability,
            Action? start = null,
            Action? stop = null,
            Func<ObservationDatabaseProbeResult>? databaseProbe = null) =>
            new(new ObservationCollectorCoordinatorOptions
            {
                ProfileId = $"test-{Path.GetFileName(Root)}",
                CandidatesDirectory = CandidatesDirectory,
                PluginName = pluginName,
                PluginInstanceId = $"{instanceId}-{Guid.NewGuid():N}",
                FranthropyVersion = version,
                WriterCapability = capability,
                DatabaseProbe = databaseProbe,
                StartCollector = start,
                StopCollector = stop,
            });

        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }
}
