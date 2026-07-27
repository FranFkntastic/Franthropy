using System.Runtime.InteropServices;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;

namespace Franthropy.Dalamud.Diagnostics;

/// <summary>
/// Dalamud adapter that binds <see cref="ClientMemoryTrustProbe"/> to the client condition flag
/// array. The official <see cref="ICondition"/> surface is read-only by design; writes go through
/// <see cref="ICondition.Address"/> plus the flag index and are gated behind a session-only arm
/// that is never persisted. This is a diagnostics instrument: it answers "who owns this byte"
/// and must not be used to build features that depend on forging condition state.
/// </summary>
public sealed class DalamudConditionTrustProbe : IDisposable
{
    public const string ServerAuthorityCaveat =
        "A held condition write only proves local persistence. Server and account restrictions are unaffected.";

    private static readonly TimeSpan HoldTickTimeout = TimeSpan.FromSeconds(5);

    private readonly ICondition condition;
    private readonly IFramework framework;
    private readonly IPluginLog log;
    private readonly object gate = new();
    private readonly Dictionary<int, ClientMemoryTrustProbe> probes = [];
    private bool armed;
    private bool disposed;

    public DalamudConditionTrustProbe(ICondition condition, IFramework framework, IPluginLog log)
    {
        this.condition = condition;
        this.framework = framework;
        this.log = log;
    }

    public bool IsArmed
    {
        get
        {
            lock (gate)
                return armed;
        }
    }

    public int MaxFlagEntries => condition.MaxEntries;

    public void Arm()
    {
        lock (gate)
        {
            armed = true;
            foreach (var probe in probes.Values)
                probe.Arm();
        }

        log.Warning("[Franthropy] Condition trust probe writes armed for this session.");
    }

    public void Disarm()
    {
        List<TrustHoldSnapshot> ended = [];
        lock (gate)
        {
            armed = false;
            foreach (var probe in probes.Values)
            {
                var snapshot = probe.Disarm(DateTimeOffset.Now);
                if (snapshot != null)
                    ended.Add(snapshot);
            }
        }

        foreach (var snapshot in ended)
        {
            log.Information(
                "[Franthropy] Condition hold ended by disarm: writes={Writes} overwrites={Overwrites}",
                snapshot.WriteCount,
                snapshot.OverwriteCount);
        }

        log.Information("[Franthropy] Condition trust probe writes disarmed for this session.");
    }

    public bool ReadFlag(int flagId)
    {
        ValidateFlagRange(flagId);
        return condition[flagId];
    }

    public IReadOnlyList<int> GetActiveFlagIds()
    {
        return Enumerable.Range(0, condition.MaxEntries)
            .Where(flagId => condition[flagId])
            .ToList();
    }

    public string GetFlagName(int flagId)
    {
        return Enum.GetName(typeof(ConditionFlag), flagId) ?? $"Unknown{flagId}";
    }

    public TrustWriteResult WriteOnce(int flagId, bool desired)
    {
        ValidateFlagRange(flagId);
        LogServerGatedCaveat(flagId);
        var result = ProbeFor(flagId).WriteOnce(desired);
        if (result.Success && !result.Stuck)
        {
            log.Warning(
                "[Franthropy] Condition write did not stick: {Flag} requested={Requested} after={After}",
                FormatFlag(flagId),
                desired,
                result.AfterValue?.ToString() ?? "unknown");
        }

        return result;
    }

    public async Task<TrustProbeVerdict> RunTestAsync(int flagId, bool desired, CancellationToken cancellationToken = default)
    {
        ValidateFlagRange(flagId);
        LogServerGatedCaveat(flagId);
        var probe = ProbeFor(flagId);
        var startedAt = DateTimeOffset.Now;

        try
        {
            probe.BeginTest(desired);
        }
        catch (InvalidOperationException)
        {
            probe.CancelTest();
            probe.BeginTest(desired);
        }

        await WaitForNextFrameworkTick(HoldTickTimeout, cancellationToken).ConfigureAwait(false);
        probe.RecordTestSample("next tick", DateTimeOffset.Now - startedAt);

        await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        probe.RecordTestSample("500ms", DateTimeOffset.Now - startedAt);

        await Task.Delay(1500, cancellationToken).ConfigureAwait(false);
        probe.RecordTestSample("2s", DateTimeOffset.Now - startedAt);

        var verdict = probe.CompleteTest();
        log.Information(
            "[Franthropy] Condition trust verdict: {Flag} target={Desired} classification={Classification} ({Reason})",
            FormatFlag(flagId),
            desired,
            verdict.Classification,
            verdict.Reason);
        return verdict;
    }

    public TrustHoldSnapshot StartHold(int flagId, bool desired, TimeSpan requestedDuration)
    {
        ValidateFlagRange(flagId);
        LogServerGatedCaveat(flagId);
        var probe = ProbeFor(flagId);
        var snapshot = probe.StartHold(desired, requestedDuration, DateTimeOffset.Now);
        lock (gate)
            framework.Update += OnFrameworkUpdate;
        log.Warning(
            "[Franthropy] Condition hold started: {Flag} desired={Desired} duration={Duration}s original={Original}",
            FormatFlag(flagId),
            desired,
            snapshot.Duration.TotalSeconds,
            snapshot.OriginalValue?.ToString() ?? "unknown");
        return snapshot;
    }

    public TrustHoldSnapshot? StopHold(int flagId, string reason = "stopped")
    {
        var snapshot = ProbeFor(flagId).StopHold(DateTimeOffset.Now, reason);
        if (snapshot != null)
        {
            log.Information(
                "[Franthropy] Condition hold {Reason}: {Flag} writes={Writes} overwrites={Overwrites}",
                reason,
                FormatFlag(flagId),
                snapshot.WriteCount,
                snapshot.OverwriteCount);
        }

        return snapshot;
    }

    public TrustHoldSnapshot GetHoldSnapshot(int flagId)
    {
        return ProbeFor(flagId).GetHoldSnapshot(DateTimeOffset.Now);
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        lock (gate)
            framework.Update -= OnFrameworkUpdate;

        foreach (var probe in probes.Values)
            probe.Disarm(DateTimeOffset.Now);
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        foreach (var pair in probes)
        {
            TrustHoldSnapshot? completed;
            try
            {
                completed = pair.Value.ObserveTick(DateTimeOffset.Now);
            }
            catch (Exception ex)
            {
                log.Error(ex, "[Franthropy] Condition hold tick failed for {Flag}.", FormatFlag(pair.Key));
                completed = pair.Value.StopHold(DateTimeOffset.Now, "failed");
            }

            if (completed is { IsActive: false, OverwriteCount: > 0 } snapshot)
            {
                log.Warning(
                    "[Franthropy] Condition hold ended: {Flag} writes={Writes} overwrites={Overwrites} reason={Reason}",
                    FormatFlag(pair.Key),
                    snapshot.WriteCount,
                    snapshot.OverwriteCount,
                    completed.EndReason ?? "unknown");
            }
        }
    }

    private ClientMemoryTrustProbe ProbeFor(int flagId)
    {
        lock (gate)
        {
            if (probes.TryGetValue(flagId, out var probe))
                return probe;

            var created = new ClientMemoryTrustProbe(
                read: () => condition[flagId],
                write: value => WriteFlagRaw(flagId, value));
            if (armed)
                created.Arm();

            probes[flagId] = created;
            return created;
        }
    }

    private void WriteFlagRaw(int flagId, bool value)
    {
        if (condition.Address == nint.Zero)
            throw new InvalidOperationException("ICondition.Address is zero; the condition array is unavailable.");

        Marshal.WriteByte(condition.Address, flagId, value ? (byte)1 : (byte)0);
    }

    private void ValidateFlagRange(int flagId)
    {
        if (flagId < 0 || flagId >= condition.MaxEntries)
            throw new ArgumentOutOfRangeException(nameof(flagId), $"Condition flag {flagId} out of range. Valid: 0-{condition.MaxEntries - 1}.");
    }

    private void LogServerGatedCaveat(int flagId)
    {
        if (flagId == (int)ConditionFlag.OnFreeTrial)
            log.Warning("[Franthropy] {Caveat}", ServerAuthorityCaveat);
    }

    private string FormatFlag(int flagId)
    {
        return $"{GetFlagName(flagId)}({flagId})";
    }

    private async Task<bool> WaitForNextFrameworkTick(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        void Handler(IFramework _)
        {
            framework.Update -= Handler;
            tcs.TrySetResult(true);
        }

        framework.Update += Handler;
        await using var registration = cancellationToken.Register(() =>
        {
            framework.Update -= Handler;
            tcs.TrySetCanceled(cancellationToken);
        }).ConfigureAwait(false);

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(timeout, cancellationToken)).ConfigureAwait(false);
        if (completed == tcs.Task)
            return await tcs.Task.ConfigureAwait(false);

        framework.Update -= Handler;
        return false;
    }
}
