using System.Numerics;

namespace Franthropy.Dalamud.AgentBridge;

/// <summary>
/// Frame-bound registry for plugin-owned ImGui controls. Plugins register only controls that
/// were actually rendered, and actions can be invoked only against the latest live surface.
/// </summary>
public sealed class AgentBridgeUiReviewRegistry
{
    private static readonly TimeSpan DefaultValidity = TimeSpan.FromSeconds(3);
    private readonly object gate = new();
    private readonly TimeSpan validity;
    private Dictionary<string, Entry> pending = new(StringComparer.Ordinal);
    private Dictionary<string, Entry> current = new(StringComparer.Ordinal);
    private long frameId;
    private DateTimeOffset renderedAtUtc = DateTimeOffset.MinValue;
    private bool frameOpen;

    public AgentBridgeUiReviewRegistry(TimeSpan? validity = null)
    {
        this.validity = validity is { } configured && configured > TimeSpan.Zero ? configured : DefaultValidity;
    }

    public void BeginFrame()
    {
        lock (gate)
        {
            if (frameOpen)
                throw new InvalidOperationException("The previous review frame was not completed.");
            pending = new Dictionary<string, Entry>(StringComparer.Ordinal);
            frameOpen = true;
        }
    }

    public void Register(
        string id,
        string label,
        AgentBridgeUiControlKind kind,
        Vector2 min,
        Vector2 max,
        bool enabled,
        bool selected,
        string? value,
        Action invoke)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentNullException.ThrowIfNull(invoke);
        if (!IsFinite(min) || !IsFinite(max) || max.X < min.X || max.Y < min.Y)
            throw new ArgumentOutOfRangeException(nameof(max), "Control bounds must be finite and non-negative in size.");

        lock (gate)
        {
            if (!frameOpen)
                throw new InvalidOperationException("Controls can only be registered while a review frame is open.");
            if (!pending.TryAdd(id, new Entry(new AgentBridgeUiControl(id, label, kind, min.X, min.Y, max.X - min.X, max.Y - min.Y, enabled, selected, value), invoke)))
                throw new InvalidOperationException($"Review control '{id}' was registered more than once in the same frame.");
        }
    }

    public AgentBridgeUiReviewFrame EndFrame()
    {
        lock (gate)
        {
            if (!frameOpen)
                throw new InvalidOperationException("No review frame is open.");
            frameOpen = false;
            renderedAtUtc = DateTimeOffset.UtcNow;
            if (!Equivalent(current, pending))
                frameId++;
            current = pending;
            return CreateFrame();
        }
    }

    public AgentBridgeUiReviewFrame Snapshot()
    {
        lock (gate)
            return CreateFrame();
    }

    /// <summary>
    /// Reviews one rendered control without cloning the complete control surface. The returned
    /// frame ID and expiry retain the same invocation safety contract as <see cref="Snapshot"/>.
    /// </summary>
    public AgentBridgeUiControlReview Review(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        lock (gate)
        {
            current.TryGetValue(id, out var entry);
            return new AgentBridgeUiControlReview(
                frameId,
                renderedAtUtc,
                renderedAtUtc == DateTimeOffset.MinValue ? DateTimeOffset.MinValue : renderedAtUtc.Add(validity),
                entry?.Control);
        }
    }

    public AgentBridgeUiControlInvocation Invoke(string id, long expectedFrameId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        Action? action;
        lock (gate)
        {
            if (frameOpen)
                return AgentBridgeUiControlInvocation.Fail("The review surface is currently being rendered.", CreateFrame());
            if (expectedFrameId != frameId)
                return AgentBridgeUiControlInvocation.Fail("The requested control surface is stale. Refresh it and retry.", CreateFrame());
            if (DateTimeOffset.UtcNow - renderedAtUtc > validity)
                return AgentBridgeUiControlInvocation.Fail("The requested control surface has expired. Refresh it and retry.", CreateFrame());
            if (!current.TryGetValue(id, out var entry))
                return AgentBridgeUiControlInvocation.Fail("The requested control is not rendered.", CreateFrame());
            if (!entry.Control.Enabled)
                return AgentBridgeUiControlInvocation.Fail("The requested control is disabled.", CreateFrame());
            action = entry.Invoke;
            // One invocation invalidates the reviewed surface immediately. The plugin must render a
            // new surface before any further action can be accepted, preventing duplicate/replayed clicks.
            current = new Dictionary<string, Entry>(StringComparer.Ordinal);
            frameId++;
            renderedAtUtc = DateTimeOffset.MinValue;
        }

        try { action(); }
        catch (Exception ex)
        {
            lock (gate)
                return AgentBridgeUiControlInvocation.Fail($"Control action failed: {ex.Message}", CreateFrame());
        }
        lock (gate)
            return AgentBridgeUiControlInvocation.Ok("Control action was invoked.", CreateFrame());
    }

    private AgentBridgeUiReviewFrame CreateFrame() => new(
        frameId,
        renderedAtUtc,
        renderedAtUtc == DateTimeOffset.MinValue ? DateTimeOffset.MinValue : renderedAtUtc.Add(validity),
        current.Values.Select(entry => entry.Control).OrderBy(control => control.Id, StringComparer.Ordinal).ToArray());

    private static bool Equivalent(IReadOnlyDictionary<string, Entry> left, IReadOnlyDictionary<string, Entry> right) =>
        left.Count == right.Count && left.All(pair => right.TryGetValue(pair.Key, out var other) && pair.Value.Control == other.Control);

    private static bool IsFinite(Vector2 value) => float.IsFinite(value.X) && float.IsFinite(value.Y);

    private sealed record Entry(AgentBridgeUiControl Control, Action Invoke);
}

public enum AgentBridgeUiControlKind
{
    Button,
    Toggle,
    Input,
    Select,
}

public sealed record AgentBridgeUiControl(
    string Id,
    string Label,
    AgentBridgeUiControlKind Kind,
    float X,
    float Y,
    float Width,
    float Height,
    bool Enabled,
    bool Selected,
    string? Value);

public sealed record AgentBridgeUiReviewFrame(
    long FrameId,
    DateTimeOffset RenderedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    IReadOnlyList<AgentBridgeUiControl> Controls);

public sealed record AgentBridgeUiControlReview(
    long FrameId,
    DateTimeOffset RenderedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    AgentBridgeUiControl? Control);

public sealed record AgentBridgeUiControlInvocation(bool Success, string Message, AgentBridgeUiReviewFrame Frame)
{
    public static AgentBridgeUiControlInvocation Ok(string message, AgentBridgeUiReviewFrame frame) => new(true, message, frame);

    public static AgentBridgeUiControlInvocation Fail(string message, AgentBridgeUiReviewFrame frame) => new(false, message, frame);
}
