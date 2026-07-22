using System.Numerics;
using System.Text.Json;

namespace Franthropy.Dalamud.AgentBridge;

/// <summary>
/// Frame-bound registry for plugin-owned ImGui controls. Plugins register only controls that
/// were actually rendered. Explicitly reviewed controls retain a short, one-use invocation lease
/// so the next ImGui frame cannot invalidate an in-flight bridge request.
/// </summary>
public sealed class AgentBridgeUiReviewRegistry
{
    private static readonly TimeSpan DefaultValidity = TimeSpan.FromSeconds(3);
    private readonly object gate = new();
    private readonly TimeSpan validity;
    private Dictionary<string, Entry> pending = new(StringComparer.Ordinal);
    private Dictionary<string, Entry> current = new(StringComparer.Ordinal);
    private readonly Dictionary<LeaseKey, ReviewedEntry> reviewed = [];
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
        ArgumentNullException.ThrowIfNull(invoke);
        Register(
            id,
            label,
            kind,
            min,
            max,
            enabled,
            selected,
            value,
            arguments: null,
            _ =>
            {
                invoke();
                return AgentBridgeUiActionResult.Ok("Control action was invoked.");
            });
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
        AgentBridgeActionArgumentSchema? arguments,
        Func<JsonElement?, AgentBridgeUiActionResult> invoke)
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
            if (!pending.TryAdd(id, new Entry(new AgentBridgeUiControl(
                    id,
                    label,
                    kind,
                    min.X,
                    min.Y,
                    max.X - min.X,
                    max.Y - min.Y,
                    enabled,
                    selected,
                    value,
                    arguments), invoke)))
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
        {
            RetainReviewed(current);
            return CreateFrame();
        }
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
            if (entry is not null)
                reviewed[new LeaseKey(frameId, id)] = new ReviewedEntry(entry, renderedAtUtc.Add(validity));
            PruneExpiredReviews();
            return new AgentBridgeUiControlReview(
                frameId,
                renderedAtUtc,
                renderedAtUtc == DateTimeOffset.MinValue ? DateTimeOffset.MinValue : renderedAtUtc.Add(validity),
                entry?.Control);
        }
    }

    public AgentBridgeUiControlInvocation Invoke(string id, long expectedFrameId, JsonElement? arguments = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        Func<JsonElement?, AgentBridgeUiActionResult>? action;
        lock (gate)
        {
            PruneExpiredReviews();
            Entry? entry;
            if (reviewed.TryGetValue(new LeaseKey(expectedFrameId, id), out var retained))
            {
                entry = retained.Entry;
            }
            else
            {
                if (frameOpen)
                    return AgentBridgeUiControlInvocation.Fail("The review surface is currently being rendered.", CreateFrame());
                if (expectedFrameId != frameId)
                    return AgentBridgeUiControlInvocation.Fail("The requested control surface is stale. Refresh it and retry.", CreateFrame());
                if (DateTimeOffset.UtcNow - renderedAtUtc > validity)
                    return AgentBridgeUiControlInvocation.Fail("The requested control surface has expired. Refresh it and retry.", CreateFrame());
                if (!current.TryGetValue(id, out entry))
                    return AgentBridgeUiControlInvocation.Fail("The requested control is not rendered.", CreateFrame());
            }
            if (!entry.Control.Enabled)
                return AgentBridgeUiControlInvocation.Fail("The requested control is disabled.", CreateFrame());
            var argumentError = AgentBridgeActionArgumentValidator.Validate(entry.Control.Arguments, arguments);
            if (argumentError is not null)
                return AgentBridgeUiControlInvocation.Fail(argumentError, CreateFrame());
            action = entry.Invoke;
            // One invocation invalidates the reviewed surface immediately. The plugin must render a
            // new surface before any further action can be accepted, preventing duplicate/replayed clicks.
            current = new Dictionary<string, Entry>(StringComparer.Ordinal);
            reviewed.Clear();
            frameId++;
            renderedAtUtc = DateTimeOffset.MinValue;
        }

        AgentBridgeUiActionResult result;
        try { result = action(arguments); }
        catch (Exception ex)
        {
            lock (gate)
                return AgentBridgeUiControlInvocation.Fail($"Control action failed: {ex.Message}", CreateFrame());
        }
        lock (gate)
            return result.Success
                ? AgentBridgeUiControlInvocation.Ok(result.Message, CreateFrame(), result)
                : AgentBridgeUiControlInvocation.Fail(result.Message, CreateFrame(), result);
    }

    private AgentBridgeUiReviewFrame CreateFrame() => new(
        frameId,
        renderedAtUtc,
        renderedAtUtc == DateTimeOffset.MinValue ? DateTimeOffset.MinValue : renderedAtUtc.Add(validity),
        current.Values.Select(entry => entry.Control).OrderBy(control => control.Id, StringComparer.Ordinal).ToArray());

    private void RetainReviewed(IReadOnlyDictionary<string, Entry> entries)
    {
        var expiresAt = renderedAtUtc.Add(validity);
        foreach (var pair in entries)
            reviewed[new LeaseKey(frameId, pair.Key)] = new ReviewedEntry(pair.Value, expiresAt);
        PruneExpiredReviews();
    }

    private void PruneExpiredReviews()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var key in reviewed.Where(pair => pair.Value.ExpiresAtUtc <= now).Select(pair => pair.Key).ToArray())
            reviewed.Remove(key);
    }

    private static bool Equivalent(IReadOnlyDictionary<string, Entry> left, IReadOnlyDictionary<string, Entry> right) =>
        left.Count == right.Count && left.All(pair => right.TryGetValue(pair.Key, out var other) && pair.Value.Control == other.Control);

    private static bool IsFinite(Vector2 value) => float.IsFinite(value.X) && float.IsFinite(value.Y);

    private sealed record Entry(AgentBridgeUiControl Control, Func<JsonElement?, AgentBridgeUiActionResult> Invoke);
    private readonly record struct LeaseKey(long FrameId, string ControlId);
    private sealed record ReviewedEntry(Entry Entry, DateTimeOffset ExpiresAtUtc);
}

public enum AgentBridgeUiControlKind
{
    Button,
    Toggle,
    Input,
    Select,
    Reveal,
    Hover,
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
    string? Value,
    AgentBridgeActionArgumentSchema? Arguments = null);

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

public sealed record AgentBridgeUiActionResult(bool Success, string Message, string? OperationId = null, object? Receipt = null)
{
    public static AgentBridgeUiActionResult Ok(string message, string? operationId = null, object? receipt = null) =>
        new(true, message, operationId, receipt);
    public static AgentBridgeUiActionResult Fail(string message, object? receipt = null) =>
        new(false, message, null, receipt);
}

public sealed record AgentBridgeUiControlInvocation(
    bool Success,
    string Message,
    AgentBridgeUiReviewFrame Frame,
    AgentBridgeUiActionResult? Action = null)
{
    public static AgentBridgeUiControlInvocation Ok(
        string message,
        AgentBridgeUiReviewFrame frame,
        AgentBridgeUiActionResult? action = null) => new(true, message, frame, action);

    public static AgentBridgeUiControlInvocation Fail(
        string message,
        AgentBridgeUiReviewFrame frame,
        AgentBridgeUiActionResult? action = null) => new(false, message, frame, action);
}

internal static class AgentBridgeActionArgumentValidator
{
    public static string? Validate(AgentBridgeActionArgumentSchema? schema, JsonElement? arguments)
    {
        var supplied = arguments is { } value && value.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined;
        if (schema is null)
            return supplied ? "This control does not accept arguments." : null;
        if (!supplied)
            return schema.Properties.Any(property => property.Required) ? "Control arguments are required." : null;
        var root = arguments!.Value;
        if (root.ValueKind != JsonValueKind.Object)
            return "Control arguments must be a JSON object.";

        var declared = schema.Properties.ToDictionary(property => property.Name, StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!declared.TryGetValue(property.Name, out var descriptor))
            {
                if (!schema.AllowAdditionalProperties)
                    return $"Control argument '{property.Name}' is not declared.";
                continue;
            }
            var error = ValidateValue(descriptor, property.Value);
            if (error is not null)
                return error;
        }

        foreach (var required in schema.Properties.Where(property => property.Required))
        {
            if (!root.TryGetProperty(required.Name, out _))
                return $"Control argument '{required.Name}' is required.";
        }
        return null;
    }

    private static string? ValidateValue(AgentBridgeActionArgumentDescriptor descriptor, JsonElement value)
    {
        switch (descriptor.Kind)
        {
            case AgentBridgeActionArgumentKind.String:
            case AgentBridgeActionArgumentKind.ItemName:
                if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
                    return $"Control argument '{descriptor.Name}' must be a non-empty string.";
                return null;
            case AgentBridgeActionArgumentKind.Boolean:
                return value.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? null
                    : $"Control argument '{descriptor.Name}' must be a boolean.";
            case AgentBridgeActionArgumentKind.Integer:
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var number))
                    return $"Control argument '{descriptor.Name}' must be an integer.";
                if (descriptor.Minimum is { } minimum && number < minimum)
                    return $"Control argument '{descriptor.Name}' must be at least {minimum}.";
                if (descriptor.Maximum is { } maximum && number > maximum)
                    return $"Control argument '{descriptor.Name}' must be at most {maximum}.";
                return null;
            case AgentBridgeActionArgumentKind.Enum:
                if (value.ValueKind != JsonValueKind.String || descriptor.AllowedValues is not { Count: > 0 } allowed ||
                    !allowed.Contains(value.GetString() ?? string.Empty, StringComparer.Ordinal))
                    return $"Control argument '{descriptor.Name}' must be one of: {string.Join(", ", descriptor.AllowedValues ?? [])}.";
                return null;
            default:
                return $"Control argument '{descriptor.Name}' has an unsupported type.";
        }
    }
}
