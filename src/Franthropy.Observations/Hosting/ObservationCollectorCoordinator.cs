using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Franthropy.Observations.Storage;
using Franthropy.Observations.V1;

namespace Franthropy.Observations.Hosting;

public sealed record ObservationCollectorCoordinatorOptions
{
    public required string ProfileId { get; init; }
    public required string CandidatesDirectory { get; init; }
    public required string PluginName { get; init; }
    public required string PluginInstanceId { get; init; }
    public required Version FranthropyVersion { get; init; }
    public int WriterCapability { get; init; } = 1;
    public int MinimumWriterCapability { get; init; } = 1;
    public Func<ObservationDatabaseProbeResult>? DatabaseProbe { get; init; }
    public Action? StartCollector { get; init; }
    public Action? StopCollector { get; init; }
}

public sealed class ObservationCollectorCoordinator : IDisposable
{
    private const long CandidateLockOffset = 1L << 40;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ObservationCollectorCoordinatorOptions options;
    private readonly AutoResetEvent changed = new(initialState: false);
    private readonly ManualResetEvent stopping = new(initialState: false);
    private readonly object stateGate = new();
    private FileSystemWatcher? watcher;
    private FileStream? candidateStream;
    private Mutex? collectorMutex;
    private EventWaitHandle? rebalanceEvent;
    private Thread? coordinatorThread;
    private ObservationLeadershipSnapshot state;
    private bool started;
    private bool disposed;
    private string? faultReason;
    private int writerCapability;

    public ObservationCollectorCoordinator(ObservationCollectorCoordinatorOptions options)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        ArgumentException.ThrowIfNullOrWhiteSpace(options.PluginName);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.PluginInstanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ProfileId);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.CandidatesDirectory);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.WriterCapability, 0);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MinimumWriterCapability, 1);
        writerCapability = options.WriterCapability;
        state = Snapshot(ObservationLeadershipState.Stopped, "The observation host has not started.");
    }

    public event EventHandler<ObservationLeadershipSnapshot>? LeadershipChanged;
    public string? LastNotificationError { get; private set; }

    public ObservationLeadershipSnapshot State
    {
        get
        {
            lock (stateGate)
                return state;
        }
    }

    public string CandidatePath { get; private set; } = string.Empty;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (started)
            return;

        Directory.CreateDirectory(options.CandidatesDirectory);
        watcher = new FileSystemWatcher(options.CandidatesDirectory, "*.json")
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            IncludeSubdirectories = false,
            EnableRaisingEvents = true,
        };
        watcher.Created += OnCandidateChanged;
        watcher.Changed += OnCandidateChanged;
        watcher.Deleted += OnCandidateChanged;
        watcher.Renamed += OnCandidateRenamed;
        watcher.Error += OnWatcherError;

        CandidatePath = Path.Combine(
            options.CandidatesDirectory,
            $"{HashName(options.PluginInstanceId)}.json");
        candidateStream = OpenCandidate(CandidatePath);
        collectorMutex = new Mutex(initiallyOwned: false, $"Local\\Franthropy.Observations.{options.ProfileId}.Collector");
        rebalanceEvent = new EventWaitHandle(
            initialState: false,
            EventResetMode.AutoReset,
            $"Local\\Franthropy.Observations.{options.ProfileId}.Rebalance");
        coordinatorThread = new Thread(Coordinate)
        {
            IsBackground = true,
            Name = $"Franthropy observations: {options.PluginName}",
        };
        started = true;
        coordinatorThread.Start();
        changed.Set();
        rebalanceEvent.Set();
    }

    public void UpdateWriterCapability(int capability)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentOutOfRangeException.ThrowIfLessThan(capability, 0);
        if (!started)
            throw new InvalidOperationException("The coordinator has not started.");
        Interlocked.Exchange(ref writerCapability, capability);
        faultReason = null;
        WriteCandidate(candidateStream!);
        changed.Set();
        rebalanceEvent!.Set();
    }

    public void ReportCollectorFault(string reason)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (!started)
            throw new InvalidOperationException("The coordinator has not started.");
        faultReason = reason;
        WriteCandidate(candidateStream!);
        changed.Set();
        rebalanceEvent!.Set();
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;

        stopping.Set();
        changed.Set();
        rebalanceEvent?.Set();
        if (coordinatorThread is not null && !coordinatorThread.Join(TimeSpan.FromSeconds(5)))
            throw new InvalidOperationException("The observation collector coordinator did not stop cleanly.");

        if (candidateStream is not null)
        {
            try { candidateStream.Unlock(CandidateLockOffset, 1); }
            catch (IOException) { }
            candidateStream.Dispose();
        }

        if (!string.IsNullOrWhiteSpace(CandidatePath))
        {
            try { File.Delete(CandidatePath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        watcher?.Dispose();
        collectorMutex?.Dispose();
        rebalanceEvent?.Dispose();
        changed.Dispose();
        stopping.Dispose();
        SetState(ObservationLeadershipState.Stopped, "The observation host stopped cleanly.");
    }

    private void Coordinate()
    {
        var ownsCollector = false;
        try
        {
            while (!stopping.WaitOne(0))
            {
                var candidates = ReadLiveCompatibleCandidates();
                var selfIsBest = candidates.Count > 0 &&
                    string.Equals(candidates[0].PluginInstanceId, options.PluginInstanceId, StringComparison.Ordinal);

                if (faultReason is not null)
                {
                    if (ownsCollector)
                    {
                        StopCollector();
                        collectorMutex!.ReleaseMutex();
                        ownsCollector = false;
                    }
                    SetState(ObservationLeadershipState.Faulted, faultReason);
                    WaitHandle.WaitAny([changed, stopping]);
                    continue;
                }

                if (ownsCollector)
                {
                    if (!selfIsBest)
                    {
                        StopCollector();
                        collectorMutex!.ReleaseMutex();
                        ownsCollector = false;
                        SetState(ObservationLeadershipState.Reader, "A better compatible host became available.");
                        continue;
                    }

                    SetState(ObservationLeadershipState.Collector, "This host owns shared observation collection.");
                    WaitHandle.WaitAny([changed, rebalanceEvent!, stopping]);
                    continue;
                }

                if (!selfIsBest)
                {
                    SetState(ObservationLeadershipState.Reader, "Another compatible host ranks ahead of this reader.");
                    WaitHandle.WaitAny([changed, stopping]);
                    continue;
                }

                SetState(ObservationLeadershipState.WaitingForOwnership, "Waiting passively for the collector lock.");
                var acquired = WaitForCollectorLock();
                if (!acquired)
                    continue;

                ownsCollector = true;
                candidates = ReadLiveCompatibleCandidates();
                selfIsBest = candidates.Count > 0 &&
                    string.Equals(candidates[0].PluginInstanceId, options.PluginInstanceId, StringComparison.Ordinal);
                if (!selfIsBest)
                {
                    collectorMutex!.ReleaseMutex();
                    ownsCollector = false;
                    continue;
                }

                try
                {
                    options.StartCollector?.Invoke();
                    SetState(ObservationLeadershipState.Collector, "This host owns shared observation collection.");
                }
                catch (Exception ex)
                {
                    faultReason = $"Collector startup failed: {ex.Message}";
                    StopCollector();
                    WriteCandidate(candidateStream!);
                    collectorMutex!.ReleaseMutex();
                    ownsCollector = false;
                    SetState(ObservationLeadershipState.Faulted, faultReason);
                }
            }
        }
        catch (Exception ex)
        {
            faultReason = $"Collector coordination failed: {ex.Message}";
            SetState(ObservationLeadershipState.Faulted, faultReason);
        }
        finally
        {
            if (ownsCollector)
            {
                StopCollector();
                collectorMutex!.ReleaseMutex();
            }
        }
    }

    private bool WaitForCollectorLock()
    {
        try
        {
            return WaitHandle.WaitAny([collectorMutex!, changed, stopping]) == 0;
        }
        catch (AbandonedMutexException ex) when (ex.MutexIndex == 0)
        {
            return true;
        }
    }

    private List<CandidateAnnouncement> ReadLiveCompatibleCandidates()
    {
        var candidates = new List<CandidateAnnouncement>();
        foreach (var path in Directory.EnumerateFiles(options.CandidatesDirectory, "*.json"))
        {
            CandidateAnnouncement? candidate;
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
                if (!IsCandidateAlive(stream))
                {
                    stream.Dispose();
                    TryDelete(path);
                    continue;
                }

                stream.Position = 0;
                candidate = JsonSerializer.Deserialize<CandidateAnnouncement>(stream, JsonOptions);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                continue;
            }

            if (candidate is not null && IsCompatible(candidate))
                candidates.Add(candidate);
        }

        candidates.Sort(CandidateComparer.Instance);
        return candidates;
    }

    private bool IsCompatible(CandidateAnnouncement candidate) =>
        candidate.WriterCapability >= Math.Max(options.MinimumWriterCapability, candidate.DatabaseMinimumWriterCapability) &&
        candidate.ContractMajor == ObservationContract.Version.Major &&
        candidate.SchemaMajor == ObservationContract.SchemaVersion.Major &&
        candidate.SchemaMinor == ObservationContract.SchemaVersion.Minor &&
        candidate.Eligible;

    private static bool IsCandidateAlive(FileStream stream)
    {
        try
        {
            stream.Lock(CandidateLockOffset, 1);
            stream.Unlock(CandidateLockOffset, 1);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
    }

    private FileStream OpenCandidate(string path)
    {
        var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite);
        try
        {
            stream.Lock(CandidateLockOffset, 1);
            WriteCandidate(stream);
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private void WriteCandidate(FileStream stream)
    {
        var capability = Volatile.Read(ref writerCapability);
        var database = options.DatabaseProbe?.Invoke();
        var minimumWriterCapability = database?.MinimumWriterCapability ?? options.MinimumWriterCapability;
        var databaseEligible = database?.CanWrite(capability) ?? capability >= minimumWriterCapability;
        var announcement = new CandidateAnnouncement(
            options.PluginName,
            options.PluginInstanceId,
            options.FranthropyVersion.ToString(),
            capability,
            Environment.ProcessId,
            ObservationContract.Version.Major,
            ObservationContract.Version.Minor,
            ObservationContract.SchemaVersion.Major,
            ObservationContract.SchemaVersion.Minor,
            minimumWriterCapability,
            faultReason is null && databaseEligible);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(announcement, JsonOptions);
        lock (stream)
        {
            stream.Position = 0;
            stream.SetLength(0);
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
    }

    private void StopCollector()
    {
        try
        {
            options.StopCollector?.Invoke();
        }
        catch (Exception ex)
        {
            faultReason ??= $"Collector shutdown failed: {ex.Message}";
        }
    }

    private void SetState(ObservationLeadershipState newState, string message)
    {
        ObservationLeadershipSnapshot snapshot;
        lock (stateGate)
        {
            snapshot = Snapshot(newState, message);
            if (state == snapshot)
                return;
            state = snapshot;
        }
        var subscribers = LeadershipChanged;
        if (subscribers is null)
            return;
        foreach (var subscriber in subscribers.GetInvocationList().Cast<EventHandler<ObservationLeadershipSnapshot>>())
        {
            try
            {
                subscriber(this, snapshot);
            }
            catch (Exception ex)
            {
                LastNotificationError = ex.Message;
            }
        }
    }

    private ObservationLeadershipSnapshot Snapshot(ObservationLeadershipState newState, string message) =>
        new(
            newState,
            options.PluginName,
            options.PluginInstanceId,
            options.FranthropyVersion.ToString(),
            Volatile.Read(ref writerCapability),
            message);

    private void OnCandidateChanged(object sender, FileSystemEventArgs e) => changed.Set();
    private void OnCandidateRenamed(object sender, RenamedEventArgs e) => changed.Set();
    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        faultReason = $"Candidate change observation failed: {e.GetException().Message}";
        changed.Set();
    }

    private static string HashName(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed record CandidateAnnouncement(
        string PluginName,
        string PluginInstanceId,
        string FranthropyVersion,
        int WriterCapability,
        int ProcessId,
        int ContractMajor,
        int ContractMinor,
        int SchemaMajor,
        int SchemaMinor,
        int DatabaseMinimumWriterCapability,
        bool Eligible);

    private sealed class CandidateComparer : IComparer<CandidateAnnouncement>
    {
        public static CandidateComparer Instance { get; } = new();

        public int Compare(CandidateAnnouncement? x, CandidateAnnouncement? y)
        {
            if (ReferenceEquals(x, y))
                return 0;
            if (x is null)
                return 1;
            if (y is null)
                return -1;

            var result = y.WriterCapability.CompareTo(x.WriterCapability);
            if (result != 0)
                return result;
            result = ParseVersion(y.FranthropyVersion).CompareTo(ParseVersion(x.FranthropyVersion));
            if (result != 0)
                return result;
            result = StringComparer.Ordinal.Compare(x.PluginName, y.PluginName);
            return result != 0 ? result : StringComparer.Ordinal.Compare(x.PluginInstanceId, y.PluginInstanceId);
        }

        private static Version ParseVersion(string value) => Version.TryParse(value, out var parsed) ? parsed : new Version(0, 0);
    }
}
