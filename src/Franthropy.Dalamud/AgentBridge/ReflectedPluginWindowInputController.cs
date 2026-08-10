using Dalamud.Bindings.ImGui;
using System.Numerics;

namespace Franthropy.Dalamud.AgentBridge;

public enum ReflectedPluginWindowInputKind
{
    Move,
    Click,
    Scroll,
    Text,
    Key,
    Drag,
    Wait,
}

public sealed record ReflectedPluginWindowInputStep(
    ReflectedPluginWindowInputKind Kind,
    float? X = null,
    float? Y = null,
    float? EndX = null,
    float? EndY = null,
    float DeltaX = 0,
    float DeltaY = 0,
    int MouseButton = 0,
    string? Text = null,
    string? Key = null,
    int Frames = 1);

public sealed record ReflectedPluginWindowInputSequence(
    int SchemaVersion,
    IReadOnlyList<ReflectedPluginWindowInputStep> Steps);

public sealed record ReflectedPluginWindowInputReceipt(
    int SchemaVersion,
    string TransactionId,
    string PluginInternalName,
    string SurfaceId,
    string RuntimeInstanceId,
    string WindowName,
    float WindowX,
    float WindowY,
    float WindowWidth,
    float WindowHeight,
    int RequestedSteps,
    int ExecutedFrames,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc);

public readonly record struct ReflectedPluginWindowFrame(
    string WindowName,
    Vector2 Position,
    Vector2 Size,
    uint ViewportId);

public interface IReflectedPluginWindowInputSink
{
    bool TryGetWindow(string windowName, out ReflectedPluginWindowFrame frame);
    void Move(Vector2 position, uint viewportId);
    void SetMouseButton(int button, bool down);
    void Scroll(float deltaX, float deltaY);
    void TypeText(string text);
    bool SetKey(string key, bool down);
}

/// <summary>
/// Queues bounded synthetic ImGui input against one current reflected-window presentation lease.
/// The controller never emits Win32 input and never targets native game UI.
/// </summary>
public sealed class ReflectedPluginWindowInputController : IDisposable
{
    public const int SchemaVersion = 1;
    private const int MaximumSteps = 32;
    private const int MaximumExpandedFrames = 120;
    private const int MaximumTextLength = 1024;
    private static readonly HashSet<string> AllowedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "Tab", "Enter", "Escape", "Space", "Backspace", "Delete", "Home", "End",
        "LeftArrow", "RightArrow", "UpArrow", "DownArrow",
    };

    private readonly Func<string, ReflectedPluginWindowPresentationTarget?> resolve;
    private readonly IReflectedPluginWindowInputSink sink;
    private readonly object gate = new();
    private PendingInteraction? active;
    private bool disposed;

    public ReflectedPluginWindowInputController(
        Func<string, ReflectedPluginWindowPresentationTarget?> resolve,
        IReflectedPluginWindowInputSink? sink = null)
    {
        this.resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));
        this.sink = sink ?? new DalamudImGuiWindowInputSink();
    }

    public Task<ReflectedPluginWindowInputReceipt> SubmitAsync(
        string transactionId,
        ReflectedPluginWindowInputSequence sequence,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transactionId);
        ArgumentNullException.ThrowIfNull(sequence);
        var commands = ValidateAndExpand(sequence);
        var target = resolve(transactionId)
            ?? throw new InvalidOperationException("The presentation transaction is stale or its plugin runtime changed.");
        if (!target.Window.IsOpen)
            throw new InvalidOperationException("The presented plugin window is no longer open.");

        PendingInteraction pending;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (active is not null)
                throw new InvalidOperationException("A reflected plugin window input sequence is already active.");
            pending = new PendingInteraction(
                transactionId,
                target.Descriptor,
                target.Window.WindowName,
                sequence.Steps.Count,
                commands,
                DateTimeOffset.UtcNow);
            active = pending;
        }
        pending.Cancellation = cancellationToken.Register(() => pending.CancelRequested = true);
        return pending.Completion.Task;
    }

    public void RenderFrame()
    {
        PendingInteraction? pending;
        lock (gate)
            pending = active;
        if (pending is null)
            return;

        try
        {
            if (pending.CancelRequested)
                throw new OperationCanceledException("The reflected plugin window input sequence was cancelled.");
            var target = resolve(pending.TransactionId)
                ?? throw new InvalidOperationException("The presentation transaction became stale before input completed.");
            if (!string.Equals(target.Descriptor.Id, pending.Descriptor.Id, StringComparison.Ordinal) ||
                !string.Equals(target.Descriptor.RuntimeInstanceId, pending.Descriptor.RuntimeInstanceId, StringComparison.Ordinal))
                throw new InvalidOperationException("The reflected plugin surface changed runtime identity before input completed.");
            if (!target.Window.IsOpen)
                throw new InvalidOperationException("The presented plugin window closed before input completed.");
            if (!sink.TryGetWindow(pending.WindowName, out var frame) || frame.Size.X <= 0 || frame.Size.Y <= 0)
                return;

            pending.LastFrame = frame;
            if (pending.Commands.Count == 0)
            {
                Complete(pending);
                return;
            }

            var command = pending.Commands.Dequeue();
            Execute(command, frame, pending);
            pending.ExecutedFrames++;
        }
        catch (Exception exception)
        {
            Fail(pending, exception);
        }
    }

    public void Dispose()
    {
        PendingInteraction? pending;
        lock (gate)
        {
            if (disposed)
                return;
            disposed = true;
            pending = active;
            active = null;
        }
        if (pending is not null)
        {
            ReleaseHeldInput(pending);
            pending.Cancellation.Dispose();
            pending.Completion.TrySetException(new ObjectDisposedException(nameof(ReflectedPluginWindowInputController)));
        }
    }

    private void Complete(PendingInteraction pending)
    {
        if (pending.HeldButtons.Count > 0 || pending.HeldKeys.Count > 0)
            throw new InvalidOperationException("The input sequence ended with a held button or key.");
        var frame = pending.LastFrame
            ?? throw new InvalidOperationException("The target plugin window never produced input bounds.");
        lock (gate)
        {
            if (!ReferenceEquals(active, pending))
                return;
            active = null;
        }
        pending.Cancellation.Dispose();
        pending.Completion.TrySetResult(new(
            SchemaVersion,
            pending.TransactionId,
            pending.Descriptor.PluginInternalName,
            pending.Descriptor.Id,
            pending.Descriptor.RuntimeInstanceId!,
            pending.WindowName,
            frame.Position.X,
            frame.Position.Y,
            frame.Size.X,
            frame.Size.Y,
            pending.RequestedSteps,
            pending.ExecutedFrames,
            pending.StartedAtUtc,
            DateTimeOffset.UtcNow));
    }

    private void Fail(PendingInteraction pending, Exception exception)
    {
        lock (gate)
        {
            if (!ReferenceEquals(active, pending))
                return;
            active = null;
        }
        ReleaseHeldInput(pending);
        pending.Cancellation.Dispose();
        if (exception is OperationCanceledException)
            pending.Completion.TrySetCanceled();
        else
            pending.Completion.TrySetException(exception);
    }

    private void ReleaseHeldInput(PendingInteraction pending)
    {
        foreach (var button in pending.HeldButtons.ToArray())
            sink.SetMouseButton(button, false);
        foreach (var key in pending.HeldKeys.ToArray())
            sink.SetKey(key, false);
        pending.HeldButtons.Clear();
        pending.HeldKeys.Clear();
    }

    private void Execute(FrameCommand command, ReflectedPluginWindowFrame frame, PendingInteraction pending)
    {
        switch (command.Kind)
        {
            case FrameCommandKind.Wait:
                return;
            case FrameCommandKind.Move:
                sink.Move(ToAbsolute(frame, command.X, command.Y), frame.ViewportId);
                return;
            case FrameCommandKind.MouseDown:
                sink.SetMouseButton(command.MouseButton, true);
                pending.HeldButtons.Add(command.MouseButton);
                return;
            case FrameCommandKind.MouseUp:
                sink.SetMouseButton(command.MouseButton, false);
                pending.HeldButtons.Remove(command.MouseButton);
                return;
            case FrameCommandKind.Scroll:
                sink.Scroll(command.DeltaX, command.DeltaY);
                return;
            case FrameCommandKind.Text:
                sink.TypeText(command.Text!);
                return;
            case FrameCommandKind.KeyDown:
                if (!sink.SetKey(command.Key!, true))
                    throw new InvalidOperationException($"ImGui key '{command.Key}' is unavailable in the loaded runtime.");
                pending.HeldKeys.Add(command.Key!);
                return;
            case FrameCommandKind.KeyUp:
                sink.SetKey(command.Key!, false);
                pending.HeldKeys.Remove(command.Key!);
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(command));
        }
    }

    private static Vector2 ToAbsolute(ReflectedPluginWindowFrame frame, float x, float y) =>
        frame.Position + new Vector2(frame.Size.X * x, frame.Size.Y * y);

    private static Queue<FrameCommand> ValidateAndExpand(ReflectedPluginWindowInputSequence sequence)
    {
        if (sequence.SchemaVersion != SchemaVersion)
            throw new ArgumentException($"Surface input schema {sequence.SchemaVersion} is unsupported; expected {SchemaVersion}.");
        if (sequence.Steps is null || sequence.Steps.Count is < 1 or > MaximumSteps)
            throw new ArgumentException($"A surface input sequence requires 1-{MaximumSteps} steps.");

        var commands = new Queue<FrameCommand>();
        foreach (var step in sequence.Steps)
        {
            var settle = Math.Clamp(step.Frames, 1, 30);
            switch (step.Kind)
            {
                case ReflectedPluginWindowInputKind.Move:
                    RequirePoint(step.X, step.Y, step.Kind);
                    commands.Enqueue(FrameCommand.Move(step.X!.Value, step.Y!.Value));
                    EnqueueWait(commands, settle);
                    break;
                case ReflectedPluginWindowInputKind.Click:
                    RequirePoint(step.X, step.Y, step.Kind);
                    RequireMouseButton(step.MouseButton);
                    commands.Enqueue(FrameCommand.Move(step.X!.Value, step.Y!.Value));
                    commands.Enqueue(FrameCommand.Wait());
                    commands.Enqueue(FrameCommand.MouseDown(step.MouseButton));
                    commands.Enqueue(FrameCommand.Wait());
                    commands.Enqueue(FrameCommand.MouseUp(step.MouseButton));
                    EnqueueWait(commands, settle);
                    break;
                case ReflectedPluginWindowInputKind.Scroll:
                    RequirePoint(step.X, step.Y, step.Kind);
                    if (!float.IsFinite(step.DeltaX) || !float.IsFinite(step.DeltaY) ||
                        Math.Abs(step.DeltaX) > 20 || Math.Abs(step.DeltaY) > 20 ||
                        (step.DeltaX == 0 && step.DeltaY == 0))
                        throw new ArgumentException("Scroll deltas must be finite, non-zero, and between -20 and 20.");
                    commands.Enqueue(FrameCommand.Move(step.X!.Value, step.Y!.Value));
                    commands.Enqueue(FrameCommand.Wait());
                    commands.Enqueue(FrameCommand.Scroll(step.DeltaX, step.DeltaY));
                    EnqueueWait(commands, settle);
                    break;
                case ReflectedPluginWindowInputKind.Text:
                    if (string.IsNullOrEmpty(step.Text) || step.Text.Length > MaximumTextLength || step.Text.Any(char.IsControl))
                        throw new ArgumentException($"Text input must contain 1-{MaximumTextLength} printable characters.");
                    commands.Enqueue(FrameCommand.TextInput(step.Text));
                    EnqueueWait(commands, settle);
                    break;
                case ReflectedPluginWindowInputKind.Key:
                    if (string.IsNullOrWhiteSpace(step.Key) || !AllowedKeys.Contains(step.Key))
                        throw new ArgumentException($"Key '{step.Key}' is not in the bounded ImGui navigation key allowlist.");
                    commands.Enqueue(FrameCommand.KeyDown(step.Key));
                    commands.Enqueue(FrameCommand.Wait());
                    commands.Enqueue(FrameCommand.KeyUp(step.Key));
                    EnqueueWait(commands, settle);
                    break;
                case ReflectedPluginWindowInputKind.Drag:
                    RequirePoint(step.X, step.Y, step.Kind);
                    RequirePoint(step.EndX, step.EndY, step.Kind);
                    RequireMouseButton(step.MouseButton);
                    var dragFrames = Math.Clamp(step.Frames, 2, 30);
                    commands.Enqueue(FrameCommand.Move(step.X!.Value, step.Y!.Value));
                    commands.Enqueue(FrameCommand.Wait());
                    commands.Enqueue(FrameCommand.MouseDown(step.MouseButton));
                    commands.Enqueue(FrameCommand.Wait());
                    for (var index = 1; index <= dragFrames; index++)
                    {
                        var amount = index / (float)dragFrames;
                        commands.Enqueue(FrameCommand.Move(
                            step.X.Value + ((step.EndX!.Value - step.X.Value) * amount),
                            step.Y.Value + ((step.EndY!.Value - step.Y.Value) * amount)));
                    }
                    commands.Enqueue(FrameCommand.MouseUp(step.MouseButton));
                    commands.Enqueue(FrameCommand.Wait());
                    break;
                case ReflectedPluginWindowInputKind.Wait:
                    EnqueueWait(commands, settle);
                    break;
                default:
                    throw new ArgumentException($"Surface input kind '{step.Kind}' is unsupported.");
            }
        }
        if (commands.Count > MaximumExpandedFrames)
            throw new ArgumentException($"The expanded surface input sequence exceeds {MaximumExpandedFrames} frames.");
        return commands;
    }

    private static void RequirePoint(float? x, float? y, ReflectedPluginWindowInputKind kind)
    {
        if (x is not { } px || y is not { } py || !float.IsFinite(px) || !float.IsFinite(py) ||
            px is < 0 or > 1 || py is < 0 or > 1)
            throw new ArgumentException($"{kind} requires normalized X and Y coordinates between 0 and 1.");
    }

    private static void RequireMouseButton(int button)
    {
        if (button is < 0 or > 2)
            throw new ArgumentException("MouseButton must be 0 (left), 1 (right), or 2 (middle).");
    }

    private static void EnqueueWait(Queue<FrameCommand> commands, int frames)
    {
        for (var index = 0; index < frames; index++)
            commands.Enqueue(FrameCommand.Wait());
    }

    private enum FrameCommandKind
    {
        Wait,
        Move,
        MouseDown,
        MouseUp,
        Scroll,
        Text,
        KeyDown,
        KeyUp,
    }

    private sealed record FrameCommand(
        FrameCommandKind Kind,
        float X = 0,
        float Y = 0,
        float DeltaX = 0,
        float DeltaY = 0,
        int MouseButton = 0,
        string? Text = null,
        string? Key = null)
    {
        public static FrameCommand Wait() => new(FrameCommandKind.Wait);
        public static FrameCommand Move(float x, float y) => new(FrameCommandKind.Move, X: x, Y: y);
        public static FrameCommand MouseDown(int button) => new(FrameCommandKind.MouseDown, MouseButton: button);
        public static FrameCommand MouseUp(int button) => new(FrameCommandKind.MouseUp, MouseButton: button);
        public static FrameCommand Scroll(float x, float y) => new(FrameCommandKind.Scroll, DeltaX: x, DeltaY: y);
        public static FrameCommand TextInput(string text) => new(FrameCommandKind.Text, Text: text);
        public static FrameCommand KeyDown(string key) => new(FrameCommandKind.KeyDown, Key: key);
        public static FrameCommand KeyUp(string key) => new(FrameCommandKind.KeyUp, Key: key);
    }

    private sealed class PendingInteraction
    {
        public PendingInteraction(
            string transactionId,
            AgentBridgePluginSurfaceDescriptor descriptor,
            string windowName,
            int requestedSteps,
            Queue<FrameCommand> commands,
            DateTimeOffset startedAtUtc)
        {
            TransactionId = transactionId;
            Descriptor = descriptor;
            WindowName = windowName;
            RequestedSteps = requestedSteps;
            Commands = commands;
            StartedAtUtc = startedAtUtc;
        }

        public string TransactionId { get; }
        public AgentBridgePluginSurfaceDescriptor Descriptor { get; }
        public string WindowName { get; }
        public int RequestedSteps { get; }
        public Queue<FrameCommand> Commands { get; }
        public DateTimeOffset StartedAtUtc { get; }
        public TaskCompletionSource<ReflectedPluginWindowInputReceipt> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public HashSet<int> HeldButtons { get; } = [];
        public HashSet<string> HeldKeys { get; } = new(StringComparer.OrdinalIgnoreCase);
        public CancellationTokenRegistration Cancellation { get; set; }
        public bool CancelRequested { get; set; }
        public int ExecutedFrames { get; set; }
        public ReflectedPluginWindowFrame? LastFrame { get; set; }
    }
}

public sealed class DalamudImGuiWindowInputSink : IReflectedPluginWindowInputSink
{
    public unsafe bool TryGetWindow(string windowName, out ReflectedPluginWindowFrame frame)
    {
        var window = ImGuiP.FindWindowByName(new ImU8String(windowName));
        if (window.IsNull || (!window.Active && !window.WasActive) || window.Hidden)
        {
            frame = default;
            return false;
        }
        frame = new(windowName, window.Pos, window.Size, window.ViewportId);
        return true;
    }

    public void Move(Vector2 position, uint viewportId)
    {
        var io = ImGui.GetIO();
        if (viewportId != 0)
            io.AddMouseViewportEvent(viewportId);
        io.AddMousePosEvent(position.X, position.Y);
    }

    public void SetMouseButton(int button, bool down) => ImGui.GetIO().AddMouseButtonEvent(button, down);

    public void Scroll(float deltaX, float deltaY) => ImGui.GetIO().AddMouseWheelEvent(deltaX, deltaY);

    public void TypeText(string text) => ImGui.GetIO().AddInputCharacters(new ImU8String(text));

    public bool SetKey(string key, bool down)
    {
        if (!Enum.TryParse<ImGuiKey>(key, true, out var parsed))
            return false;
        ImGui.GetIO().AddKeyEvent(parsed, down);
        return true;
    }
}
