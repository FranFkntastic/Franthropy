namespace Franthropy.Dalamud.AgentBridge;

/// <summary>Small thread-safe operation ledger for bridge actions which outlive their reviewed frame.</summary>
public sealed class AgentBridgeOperationRegistry
{
    private readonly object gate = new();
    private readonly Dictionary<string, AgentBridgeOperationSnapshot> operations = new(StringComparer.Ordinal);
    private readonly int capacity;

    public AgentBridgeOperationRegistry(int capacity = 64)
    {
        if (capacity < 1)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        this.capacity = capacity;
    }

    public AgentBridgeOperationSnapshot Begin(string kind, string message, bool canCancel = false, string? id = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        var now = DateTimeOffset.UtcNow;
        var operation = new AgentBridgeOperationSnapshot(
            id ?? Guid.NewGuid().ToString("N"), kind, AgentBridgeOperationState.Queued, message, now, now, CanCancel: canCancel);
        lock (gate)
        {
            if (!operations.TryAdd(operation.Id, operation))
                throw new InvalidOperationException($"Agent bridge operation '{operation.Id}' already exists.");
            TrimCore();
        }
        return operation;
    }

    public AgentBridgeOperationSnapshot Update(
        string id,
        AgentBridgeOperationState state,
        string message,
        long? current = null,
        long? total = null,
        bool? canCancel = null,
        string? errorCode = null,
        IReadOnlyDictionary<string, string>? postconditions = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        lock (gate)
        {
            if (!operations.TryGetValue(id, out var existing))
                throw new KeyNotFoundException($"Agent bridge operation '{id}' was not found.");
            if (IsTerminal(existing.State))
                throw new InvalidOperationException($"Agent bridge operation '{id}' is already terminal.");
            if (total is < 0 || current is < 0 || current > total)
                throw new ArgumentOutOfRangeException(nameof(current), "Operation progress must be non-negative and cannot exceed its total.");
            var updated = existing with
            {
                State = state,
                Message = message,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                Current = current,
                Total = total,
                CanCancel = canCancel ?? existing.CanCancel,
                ErrorCode = errorCode,
                Postconditions = postconditions,
            };
            operations[id] = updated;
            return updated;
        }
    }

    public AgentBridgeOperationSnapshot? Get(string id)
    {
        lock (gate) return operations.GetValueOrDefault(id);
    }

    public IReadOnlyList<AgentBridgeOperationSnapshot> Snapshot()
    {
        lock (gate) return operations.Values.OrderByDescending(operation => operation.UpdatedAtUtc).ToArray();
    }

    private void TrimCore()
    {
        foreach (var id in operations.Values
                     .Where(operation => IsTerminal(operation.State))
                     .OrderBy(operation => operation.UpdatedAtUtc)
                     .Take(Math.Max(0, operations.Count - capacity))
                     .Select(operation => operation.Id)
                     .ToArray())
            operations.Remove(id);
    }

    private static bool IsTerminal(AgentBridgeOperationState state) =>
        state is AgentBridgeOperationState.Succeeded or AgentBridgeOperationState.Failed or AgentBridgeOperationState.Cancelled;
}
