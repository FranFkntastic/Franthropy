using Dalamud.Interface.Windowing;
using System.Numerics;

namespace Franthropy.Dalamud.AgentBridge;

/// <summary>
/// Owns one short-lived reversible presentation lease for a discovered shared Dalamud window.
/// Runtime identity is re-proved before restoration so a plugin reload cannot redirect writes.
/// </summary>
public sealed class ReflectedPluginWindowPresentationManager
{
    private static readonly TimeSpan DefaultLifetime = TimeSpan.FromSeconds(10);
    private readonly Func<string, ReflectedPluginWindowPresentationTarget?> resolve;
    private readonly TimeSpan lifetime;
    private Transaction? active;

    public ReflectedPluginWindowPresentationManager(
        Func<string, ReflectedPluginWindowPresentationTarget?> resolve,
        TimeSpan? lifetime = null)
    {
        this.resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));
        this.lifetime = lifetime is { } configured && configured > TimeSpan.Zero ? configured : DefaultLifetime;
    }

    public AgentBridgePluginSurfacePresentationReceipt Begin(string surfaceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(surfaceId);
        Expire(DateTimeOffset.UtcNow);
        if (active is not null)
            throw new InvalidOperationException("A reflected plugin surface presentation is already active.");
        var resolved = resolve(surfaceId)
            ?? throw new InvalidOperationException("The requested surface is not a current reversible reflected window.");
        if (resolved.Descriptor.Provenance != AgentBridgeSurfaceProvenance.ReflectedWindowSystem ||
            resolved.Descriptor.Authority != AgentBridgeSurfaceAuthority.ReversiblePresentation ||
            string.IsNullOrWhiteSpace(resolved.Descriptor.RuntimeInstanceId))
            throw new InvalidOperationException("The requested surface does not carry reversible reflected-window authority.");

        var window = resolved.Window;
        var requestedAt = DateTimeOffset.UtcNow;
        var transaction = new Transaction(
            Guid.NewGuid().ToString("N"),
            resolved.Descriptor.PluginInternalName,
            resolved.Descriptor.Id,
            resolved.Descriptor.RuntimeInstanceId,
            requestedAt,
            requestedAt.Add(lifetime),
            window.IsOpen,
            window.IsFocused,
            window.Collapsed,
            window.RequestFocus,
            window.Position,
            window.Size,
            resolved.Descriptor);
        try
        {
            window.IsOpen = true;
            window.Collapsed = false;
            window.RequestFocus = true;
            var presented = resolve(surfaceId);
            if (presented is null ||
                !string.Equals(presented.Descriptor.RuntimeInstanceId, transaction.RuntimeInstanceId, StringComparison.Ordinal))
                throw new InvalidOperationException("The plugin surface changed runtime identity while presentation was beginning.");
            active = transaction;
            return new AgentBridgePluginSurfacePresentationReceipt(
                transaction.Id,
                transaction.PluginInternalName,
                transaction.SurfaceId,
                transaction.RuntimeInstanceId,
                transaction.RequestedAtUtc,
                DateTimeOffset.UtcNow,
                transaction.ExpiresAtUtc,
                transaction.Before,
                presented.Descriptor);
        }
        catch
        {
            RestoreWindow(window, transaction);
            throw;
        }
    }

    public AgentBridgePluginSurfacePresentationResult Restore(string transactionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transactionId);
        Expire(DateTimeOffset.UtcNow);
        if (active is null)
            return new(false, "No reflected plugin surface presentation is active.");
        if (!string.Equals(active.Id, transactionId, StringComparison.Ordinal))
            return new(false, "The presentation transaction identifier is stale or mismatched.");
        return RestoreActive("The prior plugin window state was restored.");
    }

    public AgentBridgePluginSurfacePresentationResult? Expire(DateTimeOffset now)
    {
        return active is not null && now >= active.ExpiresAtUtc
            ? RestoreActive("The presentation lease expired and prior state was restored.")
            : null;
    }

    public AgentBridgePluginSurfacePresentationResult? CancelActive() =>
        active is null ? null : RestoreActive("The presentation was cancelled and prior state was restored.");

    public AgentBridgePluginSurfaceDescriptor? GetActiveSurface(string transactionId)
        => GetActiveTarget(transactionId)?.Descriptor;

    public ReflectedPluginWindowPresentationTarget? GetActiveTarget(string transactionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transactionId);
        Expire(DateTimeOffset.UtcNow);
        if (active is null || !string.Equals(active.Id, transactionId, StringComparison.Ordinal))
            return null;
        var resolved = resolve(active.SurfaceId);
        return resolved is not null &&
               string.Equals(resolved.Descriptor.RuntimeInstanceId, active.RuntimeInstanceId, StringComparison.Ordinal)
            ? resolved
            : null;
    }

    private AgentBridgePluginSurfacePresentationResult RestoreActive(string message)
    {
        var transaction = active!;
        active = null;
        var resolved = resolve(transaction.SurfaceId);
        if (resolved is null ||
            !string.Equals(resolved.Descriptor.RuntimeInstanceId, transaction.RuntimeInstanceId, StringComparison.Ordinal))
        {
            return new(
                false,
                "The original plugin runtime is no longer loaded; no state was written to its replacement.",
                transaction.Id);
        }
        try
        {
            RestoreWindow(resolved.Window, transaction);
            return new(true, message, transaction.Id, resolve(transaction.SurfaceId)?.Descriptor);
        }
        catch (Exception exception)
        {
            return new(
                false,
                $"The prior plugin window state could not be fully restored: {exception.Message}",
                transaction.Id);
        }
    }

    private static void RestoreWindow(IWindow window, Transaction transaction)
    {
        window.RequestFocus = transaction.WasRequestFocus;
        window.IsFocused = transaction.WasFocused;
        window.Collapsed = transaction.WasCollapsed;
        window.Position = transaction.WasPosition;
        window.Size = transaction.WasSize;
        window.IsOpen = transaction.WasOpen;
    }

    private sealed record Transaction(
        string Id,
        string PluginInternalName,
        string SurfaceId,
        string RuntimeInstanceId,
        DateTimeOffset RequestedAtUtc,
        DateTimeOffset ExpiresAtUtc,
        bool WasOpen,
        bool WasFocused,
        bool? WasCollapsed,
        bool WasRequestFocus,
        Vector2? WasPosition,
        Vector2? WasSize,
        AgentBridgePluginSurfaceDescriptor Before);
}

public sealed record ReflectedPluginWindowPresentationTarget(
    AgentBridgePluginSurfaceDescriptor Descriptor,
    IWindow Window);
