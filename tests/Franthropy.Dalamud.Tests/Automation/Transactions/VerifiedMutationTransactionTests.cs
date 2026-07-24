using Franthropy.Dalamud.Automation.Transactions;

namespace Franthropy.Dalamud.Tests.Automation.Transactions;

public sealed class VerifiedMutationTransactionTests
{
    [Fact]
    public async Task VerifiedAttempt_CommitsBeforeResolving()
    {
        var persistence = new RecordingPersistence();

        var attempt = await VerifiedMutationTransaction.ExecuteAsync(
            "intent",
            persistence,
            _ =>
            {
                persistence.Events.Add("execute");
                return Task.FromResult(VerifiedMutationAttempt<string, int>.Verified("done", 7));
            });

        Assert.Equal("done", attempt.Result);
        Assert.Equal(["arm:intent", "execute", "commit:intent:7", "resolve:intent"], persistence.Events);
    }

    [Fact]
    public async Task UnchangedAttempt_ResolvesWithoutCommitOrInvalidation()
    {
        var persistence = new RecordingPersistence();

        await VerifiedMutationTransaction.ExecuteAsync(
            "intent",
            persistence,
            _ => Task.FromResult(VerifiedMutationAttempt<string, int>.Unchanged("unchanged")));

        Assert.Equal(["arm:intent", "resolve:intent"], persistence.Events);
    }

    [Fact]
    public async Task IndeterminateAttempt_InvalidatesBeforeResolving()
    {
        var persistence = new RecordingPersistence();

        await VerifiedMutationTransaction.ExecuteAsync(
            "intent",
            persistence,
            _ => Task.FromResult(VerifiedMutationAttempt<string, int>.Indeterminate("unknown")));

        Assert.Equal(["arm:intent", "invalidate:intent", "resolve:intent"], persistence.Events);
    }

    [Fact]
    public async Task ActionException_InvalidatesAndRethrows()
    {
        var persistence = new RecordingPersistence();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            VerifiedMutationTransaction.ExecuteAsync<string, string, int>(
                "intent",
                persistence,
                _ => throw new InvalidOperationException("action failed")));

        Assert.Equal("action failed", exception.Message);
        Assert.Equal(["arm:intent", "invalidate:intent", "resolve:intent"], persistence.Events);
    }

    [Fact]
    public async Task CommitException_InvalidatesAndRethrows()
    {
        var persistence = new RecordingPersistence { CommitError = new IOException("commit failed") };

        var exception = await Assert.ThrowsAsync<IOException>(() =>
            VerifiedMutationTransaction.ExecuteAsync(
                "intent",
                persistence,
                _ => Task.FromResult(VerifiedMutationAttempt<string, int>.Verified("done", 7))));

        Assert.Equal("commit failed", exception.Message);
        Assert.Equal(
            ["arm:intent", "commit:intent:7", "invalidate:intent", "resolve:intent"],
            persistence.Events);
    }

    [Fact]
    public async Task CancellationAfterArming_StillFinalizesEvidence()
    {
        var persistence = new RecordingPersistence();
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            VerifiedMutationTransaction.ExecuteAsync<string, string, int>(
                "intent",
                persistence,
                token =>
                {
                    cancellation.Cancel();
                    token.ThrowIfCancellationRequested();
                    return Task.FromResult(VerifiedMutationAttempt<string, int>.Unchanged("unreachable"));
                },
                cancellation.Token));

        Assert.Equal(["arm:intent", "invalidate:intent", "resolve:intent"], persistence.Events);
    }

    [Fact]
    public async Task ArmException_DoesNotTouchUnarmedEvidence()
    {
        var persistence = new RecordingPersistence { ArmError = new IOException("arm failed") };

        var exception = await Assert.ThrowsAsync<IOException>(() =>
            VerifiedMutationTransaction.ExecuteAsync(
                "intent",
                persistence,
                _ => Task.FromResult(VerifiedMutationAttempt<string, int>.Verified("done", 7))));

        Assert.Equal("arm failed", exception.Message);
        Assert.Equal(["arm:intent"], persistence.Events);
    }

    private sealed class RecordingPersistence : IVerifiedMutationPersistence<string, int>
    {
        public List<string> Events { get; } = [];
        public Exception? ArmError { get; init; }
        public Exception? CommitError { get; init; }

        public ValueTask ArmAsync(string intent)
        {
            Events.Add($"arm:{intent}");
            return ArmError is null ? ValueTask.CompletedTask : ValueTask.FromException(ArmError);
        }

        public ValueTask CommitAsync(string intent, int mutation)
        {
            Events.Add($"commit:{intent}:{mutation}");
            return CommitError is null ? ValueTask.CompletedTask : ValueTask.FromException(CommitError);
        }

        public ValueTask InvalidateAsync(string intent)
        {
            Events.Add($"invalidate:{intent}");
            return ValueTask.CompletedTask;
        }

        public ValueTask ResolveAsync(string intent)
        {
            Events.Add($"resolve:{intent}");
            return ValueTask.CompletedTask;
        }
    }
}
