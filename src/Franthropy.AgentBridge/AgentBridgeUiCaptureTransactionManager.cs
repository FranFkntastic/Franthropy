namespace Franthropy.Dalamud.AgentBridge;

/// <summary>
/// Coordinates a single short-lived, frame-confirmed UI presentation for capture. The plugin
/// remains responsible for rendering only its explicitly named surface and applying viewport
/// placement while <see cref="ShouldPresentInMainViewport"/> is true.
/// </summary>
public sealed class AgentBridgeUiCaptureTransactionManager
{
    private static readonly TimeSpan DefaultLifetime = TimeSpan.FromSeconds(8);
    private const int DefaultMinimumRenderedFrames = 6;
    private readonly object gate = new();
    private readonly Func<bool> isWindowOpen;
    private readonly Action<bool> setWindowOpen;
    private readonly Func<bool> isWindowCollapsed;
    private readonly Action<bool> requestWindowCollapsed;
    private readonly Action<bool, bool> restoreWindowCollapseState;
    private readonly Action? beginPresentation;
    private readonly Action? restorePresentation;
    private readonly TimeSpan lifetime;
    private readonly int minimumRenderedFrames;
    private Transaction? active;

    public AgentBridgeUiCaptureTransactionManager(
        Func<bool> isWindowOpen,
        Action<bool> setWindowOpen,
        Func<bool> isWindowCollapsed,
        Action<bool> requestWindowCollapsed,
        Action<bool, bool> restoreWindowCollapseState,
        TimeSpan? lifetime = null,
        Action? beginPresentation = null,
        Action? restorePresentation = null,
        int minimumRenderedFrames = DefaultMinimumRenderedFrames)
    {
        this.isWindowOpen = isWindowOpen ?? throw new ArgumentNullException(nameof(isWindowOpen));
        this.setWindowOpen = setWindowOpen ?? throw new ArgumentNullException(nameof(setWindowOpen));
        this.isWindowCollapsed = isWindowCollapsed ?? throw new ArgumentNullException(nameof(isWindowCollapsed));
        this.requestWindowCollapsed = requestWindowCollapsed ?? throw new ArgumentNullException(nameof(requestWindowCollapsed));
        this.restoreWindowCollapseState = restoreWindowCollapseState ?? throw new ArgumentNullException(nameof(restoreWindowCollapseState));
        this.beginPresentation = beginPresentation;
        this.restorePresentation = restorePresentation;
        this.lifetime = lifetime is { } configured && configured > TimeSpan.Zero ? configured : DefaultLifetime;
        this.minimumRenderedFrames = minimumRenderedFrames > 0
            ? minimumRenderedFrames
            : throw new ArgumentOutOfRangeException(nameof(minimumRenderedFrames));
    }

    public AgentBridgeUiCaptureTransactionHandle Begin(string target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        lock (gate)
        {
            ExpireCore(DateTimeOffset.UtcNow);
            if (active != null)
                throw new InvalidOperationException("A UI capture presentation is already active.");

            var now = DateTimeOffset.UtcNow;
            active = new Transaction(
                Guid.NewGuid().ToString("N"), target, now, now.Add(lifetime),
                isWindowOpen(), isWindowCollapsed());
            try
            {
                setWindowOpen(true);
                requestWindowCollapsed(false);
                beginPresentation?.Invoke();
            }
            catch
            {
                RestoreAndClear(active, new InvalidOperationException("The UI capture presentation could not begin."));
                throw;
            }
            return new AgentBridgeUiCaptureTransactionHandle(active.Id, active.Target, active.ExpiresAtUtc, active.Ready.Task);
        }
    }

    public bool ShouldPresentInMainViewport(string target)
    {
        lock (gate)
        {
            ExpireCore(DateTimeOffset.UtcNow);
            return active != null && string.Equals(active.Target, target, StringComparison.Ordinal);
        }
    }

    public void MarkRendered(string target, long frameId)
    {
        lock (gate)
        {
            ExpireCore(DateTimeOffset.UtcNow);
            if (active == null || active.Ready.Task.IsCompleted || !string.Equals(active.Target, target, StringComparison.Ordinal))
                return;
            active.RenderedFrameCount++;
            if (active.RenderedFrameCount < minimumRenderedFrames)
                return;
            var readyAt = DateTimeOffset.UtcNow;
            active.Ready.TrySetResult(new AgentBridgeUiCaptureTransactionReceipt(
                active.Id, active.Target, frameId, active.RequestedAtUtc, readyAt, active.ExpiresAtUtc));
        }
    }

    public AgentBridgeUiCaptureTransactionResult Complete(string transactionId) => Finish(transactionId, true);

    public AgentBridgeUiCaptureTransactionResult Cancel(string transactionId) => Finish(transactionId, false);

    public void CancelActive()
    {
        lock (gate)
        {
            if (active != null)
                RestoreAndClear(active, new OperationCanceledException("The UI capture presentation was cancelled."));
        }
    }

    private AgentBridgeUiCaptureTransactionResult Finish(string transactionId, bool completed)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transactionId);
        lock (gate)
        {
            ExpireCore(DateTimeOffset.UtcNow);
            if (active == null)
                return AgentBridgeUiCaptureTransactionResult.Fail("No UI capture presentation is active.");
            if (!string.Equals(active.Id, transactionId, StringComparison.Ordinal))
                return AgentBridgeUiCaptureTransactionResult.Fail("The UI capture transaction identifier is stale or mismatched.");
            var id = active.Id;
            RestoreAndClear(active, completed ? null : new OperationCanceledException("The UI capture presentation was cancelled."));
            return AgentBridgeUiCaptureTransactionResult.Ok(
                completed ? "UI capture presentation completed and prior state was restored." : "UI capture presentation cancelled and prior state was restored.", id);
        }
    }

    private void ExpireCore(DateTimeOffset now)
    {
        if (active != null && now >= active.ExpiresAtUtc)
            RestoreAndClear(active, new TimeoutException("The UI capture presentation expired before completion."));
    }

    private void RestoreAndClear(Transaction transaction, Exception? readyFailure)
    {
        if (readyFailure != null)
            transaction.Ready.TrySetException(readyFailure);
        setWindowOpen(transaction.WasOpen);
        restoreWindowCollapseState(transaction.WasOpen, transaction.WasCollapsed);
        restorePresentation?.Invoke();
        active = null;
    }

    private sealed class Transaction
    {
        public Transaction(string id, string target, DateTimeOffset requestedAtUtc, DateTimeOffset expiresAtUtc, bool wasOpen, bool wasCollapsed)
        {
            Id = id;
            Target = target;
            RequestedAtUtc = requestedAtUtc;
            ExpiresAtUtc = expiresAtUtc;
            WasOpen = wasOpen;
            WasCollapsed = wasCollapsed;
        }

        public string Id { get; }
        public string Target { get; }
        public DateTimeOffset RequestedAtUtc { get; }
        public DateTimeOffset ExpiresAtUtc { get; }
        public bool WasOpen { get; }
        public bool WasCollapsed { get; }
        public int RenderedFrameCount { get; set; }
        public TaskCompletionSource<AgentBridgeUiCaptureTransactionReceipt> Ready { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

public sealed record AgentBridgeUiCaptureTransactionHandle(
    string TransactionId,
    string Target,
    DateTimeOffset ExpiresAtUtc,
    Task<AgentBridgeUiCaptureTransactionReceipt> Ready);

public sealed record AgentBridgeUiCaptureTransactionResult(bool Success, string Message, string? TransactionId)
{
    public static AgentBridgeUiCaptureTransactionResult Ok(string message, string transactionId) => new(true, message, transactionId);
    public static AgentBridgeUiCaptureTransactionResult Fail(string message) => new(false, message, null);
}
