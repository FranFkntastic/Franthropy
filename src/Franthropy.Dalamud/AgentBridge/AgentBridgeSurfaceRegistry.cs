namespace Franthropy.Dalamud.AgentBridge;

/// <summary>
/// Runtime registry for explicitly supported surfaces. The registry is the source of both
/// presentation dispatch and advertised surface metadata, so those two views cannot drift.
/// </summary>
public sealed class AgentBridgeSurfaceRegistry
{
    private readonly object gate = new();
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private long catalogRevision = 1;

    public long CatalogRevision
    {
        get { lock (gate) return catalogRevision; }
    }

    public void Register(AgentBridgeReviewSurfaceDescriptor descriptor, Action present)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(present);
        ArgumentException.ThrowIfNullOrWhiteSpace(descriptor.Id);
        lock (gate)
        {
            if (entries.TryGetValue(descriptor.Id, out var current) && current.Descriptor == descriptor)
            {
                entries[descriptor.Id] = new Entry(descriptor, present);
                return;
            }
            entries[descriptor.Id] = new Entry(descriptor, present);
            catalogRevision++;
        }
    }

    public IReadOnlyList<AgentBridgeReviewSurfaceDescriptor> Snapshot()
    {
        lock (gate)
            return entries.Values
                .Select(entry => entry.Descriptor)
                .OrderBy(descriptor => descriptor.Order)
                .ThenBy(descriptor => descriptor.Id, StringComparer.Ordinal)
                .ToArray();
    }

    public bool TryPresent(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        Action? present;
        lock (gate)
            present = entries.TryGetValue(id, out var entry) ? entry.Present : null;
        if (present is null)
            return false;
        present();
        return true;
    }

    private sealed record Entry(AgentBridgeReviewSurfaceDescriptor Descriptor, Action Present);
}
