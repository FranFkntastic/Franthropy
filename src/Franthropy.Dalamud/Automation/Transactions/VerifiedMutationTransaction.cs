using System.Runtime.ExceptionServices;

namespace Franthropy.Dalamud.Automation.Transactions;

public enum VerifiedMutationEvidence
{
    Unchanged,
    Verified,
    Indeterminate,
}

public sealed class VerifiedMutationAttempt<TResult, TMutation>
{
    private VerifiedMutationAttempt(
        TResult result,
        VerifiedMutationEvidence evidence,
        TMutation? mutation,
        bool hasMutation)
    {
        Result = result;
        Evidence = evidence;
        Mutation = mutation;
        HasMutation = hasMutation;
    }

    public TResult Result { get; }
    public VerifiedMutationEvidence Evidence { get; }
    public TMutation? Mutation { get; }
    public bool HasMutation { get; }

    public static VerifiedMutationAttempt<TResult, TMutation> Unchanged(TResult result) =>
        new(result, VerifiedMutationEvidence.Unchanged, default, false);

    public static VerifiedMutationAttempt<TResult, TMutation> Verified(TResult result, TMutation mutation) =>
        new(result, VerifiedMutationEvidence.Verified, mutation, true);

    public static VerifiedMutationAttempt<TResult, TMutation> Indeterminate(TResult result) =>
        new(result, VerifiedMutationEvidence.Indeterminate, default, false);
}

/// <summary>
/// Supplies product-owned durable evidence operations to the reusable mutation transaction.
/// Finalization callbacks deliberately do not accept a cancellation token: once a live action
/// begins, committing or invalidating its evidence must survive caller cancellation.
/// </summary>
public interface IVerifiedMutationPersistence<in TIntent, in TMutation>
{
    ValueTask ArmAsync(TIntent intent);
    ValueTask CommitAsync(TIntent intent, TMutation mutation);
    ValueTask InvalidateAsync(TIntent intent);
    ValueTask ResolveAsync(TIntent intent);
}

/// <summary>
/// Executes one externally observable mutation behind a durable recovery marker.
/// Verified mutations commit exact evidence; ambiguous outcomes invalidate it; unchanged
/// outcomes merely clear the marker. Any exception after arming is treated as indeterminate.
/// </summary>
public static class VerifiedMutationTransaction
{
    public static async Task<VerifiedMutationAttempt<TResult, TMutation>> ExecuteAsync<TIntent, TResult, TMutation>(
        TIntent intent,
        IVerifiedMutationPersistence<TIntent, TMutation> persistence,
        Func<CancellationToken, Task<VerifiedMutationAttempt<TResult, TMutation>>> executeAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(persistence);
        ArgumentNullException.ThrowIfNull(executeAsync);

        cancellationToken.ThrowIfCancellationRequested();
        await persistence.ArmAsync(intent).ConfigureAwait(false);

        try
        {
            var attempt = await executeAsync(cancellationToken).ConfigureAwait(false);
            switch (attempt.Evidence)
            {
                case VerifiedMutationEvidence.Unchanged:
                    await persistence.ResolveAsync(intent).ConfigureAwait(false);
                    break;

                case VerifiedMutationEvidence.Verified:
                    if (!attempt.HasMutation)
                        throw new InvalidOperationException("A verified mutation attempt did not provide mutation evidence.");
                    await persistence.CommitAsync(intent, attempt.Mutation!).ConfigureAwait(false);
                    await persistence.ResolveAsync(intent).ConfigureAwait(false);
                    break;

                case VerifiedMutationEvidence.Indeterminate:
                    await persistence.InvalidateAsync(intent).ConfigureAwait(false);
                    await persistence.ResolveAsync(intent).ConfigureAwait(false);
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(attempt.Evidence), attempt.Evidence, "Unknown mutation evidence.");
            }

            return attempt;
        }
        catch (Exception operationError)
        {
            try
            {
                await persistence.InvalidateAsync(intent).ConfigureAwait(false);
                await persistence.ResolveAsync(intent).ConfigureAwait(false);
            }
            catch (Exception recoveryError)
            {
                throw new AggregateException(
                    "The mutation failed and its evidence could not be durably invalidated.",
                    operationError,
                    recoveryError);
            }

            ExceptionDispatchInfo.Capture(operationError).Throw();
            throw;
        }
    }
}
