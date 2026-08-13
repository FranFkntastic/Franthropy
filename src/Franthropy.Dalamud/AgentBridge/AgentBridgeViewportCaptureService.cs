using System.Security.Cryptography;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;

namespace Franthropy.Dalamud.AgentBridge;

/// <summary>Captures a rendered ImGui viewport region into the authenticated bridge handoff.</summary>
public sealed class AgentBridgeViewportCaptureService : IDisposable
{
    private readonly string captureDirectory;
    private readonly string pluginInstanceId;
    private readonly string captureLabel;
    private readonly Func<AgentBridgeCaptureRegion?> captureRegion;
    private readonly Func<Action, CancellationToken, Task> dispatchOnFramework;
    private readonly ITextureProvider textureProvider;
    private readonly ITextureReadbackProvider readbackProvider;
    private readonly SemaphoreSlim captureLock = new(1, 1);
    private readonly object pendingWindowGate = new();
    private PendingWindowCapture? pendingWindowCapture;

    public AgentBridgeViewportCaptureService(
        string configDirectory,
        string pluginInstanceId,
        string captureLabel,
        Func<AgentBridgeCaptureRegion?> captureRegion,
        Func<Action, CancellationToken, Task> dispatchOnFramework,
        ITextureProvider textureProvider,
        ITextureReadbackProvider readbackProvider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginInstanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(captureLabel);
        this.pluginInstanceId = pluginInstanceId;
        this.captureLabel = captureLabel;
        this.captureRegion = captureRegion ?? throw new ArgumentNullException(nameof(captureRegion));
        this.dispatchOnFramework = dispatchOnFramework ?? throw new ArgumentNullException(nameof(dispatchOnFramework));
        this.textureProvider = textureProvider ?? throw new ArgumentNullException(nameof(textureProvider));
        this.readbackProvider = readbackProvider ?? throw new ArgumentNullException(nameof(readbackProvider));
        captureDirectory = Path.Combine(configDirectory, "agent-bridge", "captures");
    }

    public async Task<AgentBridgeCaptureReceipt> CaptureAsync(
        bool fullViewport,
        CancellationToken cancellationToken = default)
    {
        await captureLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var region = captureRegion();
            if (!fullViewport && (region is null || !region.IsRecent()))
                throw new InvalidOperationException("The requested plugin surface is not currently rendered.");

            Task<IDalamudTextureWrap>? textureTask = null;
            uint? capturedViewportId = null;
            await dispatchOnFramework(() =>
            {
                var currentRegion = captureRegion();
                if (!fullViewport && (currentRegion is null || !currentRegion.IsRecent()))
                    throw new InvalidOperationException("The requested plugin surface is not currently rendered.");

                var viewportId = ResolveCaptureViewportId(
                    fullViewport,
                    ImGui.GetMainViewport().ID,
                    currentRegion);
                capturedViewportId = viewportId;

                textureTask = textureProvider.CreateFromImGuiViewportAsync(
                    new ImGuiViewportTextureArgs
                    {
                        ViewportId = viewportId,
                        AutoUpdate = false,
                        TakeBeforeImGuiRender = false,
                        KeepTransparency = false,
                        Uv0 = fullViewport ? default : currentRegion!.GetUv0(),
                        Uv1 = fullViewport ? default : currentRegion!.GetUv1(),
                    },
                    $"{captureLabel} agent bridge viewport capture",
                    cancellationToken);
            }, cancellationToken).ConfigureAwait(false);

            using var texture = await (textureTask ??
                throw new InvalidOperationException("Viewport capture was not scheduled.")).ConfigureAwait(false);
            return await PersistCaptureAsync(
                texture,
                fullViewport ? "FullViewport" : "PluginWindow",
                "ImGuiViewport",
                capturedViewportId,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            captureLock.Release();
        }
    }

    /// <summary>
    /// Captures one rendered ImGui window from its draw list instead of leasing the platform
    /// viewport back buffer. This is the safe path for detachable plugin windows because a
    /// platform viewport may be destroyed or recreated between presentation and post-render
    /// texture readback during login, logout, display, or docking transitions.
    /// </summary>
    public async Task<AgentBridgeCaptureReceipt> CaptureWindowAsync(
        Func<string> windowName,
        string scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(windowName);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        await captureLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var pending = new PendingWindowCapture(
                windowName,
                new(TaskCreationOptions.RunContinuationsAsynchronously));
            lock (pendingWindowGate)
            {
                if (pendingWindowCapture is not null)
                    throw new InvalidOperationException("A plugin window capture is already waiting for an ImGui draw frame.");
                pendingWindowCapture = pending;
            }

            try
            {
                var rendered = await pending.Completion.Task
                    .WaitAsync(TimeSpan.FromSeconds(2), cancellationToken)
                    .ConfigureAwait(false);
                using (rendered.Texture)
                {
                    return await PersistCaptureAsync(
                        rendered.Texture,
                        scope,
                        "ImGuiDrawList",
                        rendered.ViewportId,
                        cancellationToken).ConfigureAwait(false);
                }
            }
            catch (TimeoutException exception)
            {
                throw new InvalidOperationException(
                    "Plugin surface capture bounds were not rendered during the two-second ImGui draw lease.",
                    exception);
            }
            finally
            {
                lock (pendingWindowGate)
                {
                    if (ReferenceEquals(pendingWindowCapture, pending))
                        pendingWindowCapture = null;
                }
            }
        }
        finally
        {
            captureLock.Release();
        }
    }

    /// <summary>Completes a pending draw-list capture after the owning window has rendered.</summary>
    public unsafe void RenderPendingWindowCapture()
    {
        PendingWindowCapture? pending;
        lock (pendingWindowGate)
            pending = pendingWindowCapture;
        if (pending is null || pending.Completion.Task.IsCompleted)
            return;

        try
        {
            var name = pending.WindowName();
            if (string.IsNullOrWhiteSpace(name))
                throw new InvalidOperationException("The plugin surface did not provide a capture window name.");
            var window = ImGuiP.FindWindowByName(new ImU8String(name));
            if (window.IsNull || (!window.Active && !window.WasActive) || window.Hidden)
                return;

            var texture = textureProvider.CreateDrawListTexture($"{captureLabel} agent bridge window capture");
            try
            {
                texture.ResizeAndDrawWindow(window, System.Numerics.Vector2.One);
                if (texture.Width <= 0 || texture.Height <= 0)
                    throw new InvalidOperationException(
                        $"Plugin surface capture bounds are unavailable because '{name}' rendered with zero size.");
                if (!pending.Completion.TrySetResult(new(
                        texture,
                        window.ViewportId == 0 ? null : window.ViewportId)))
                    texture.Dispose();
            }
            catch
            {
                texture.Dispose();
                throw;
            }
        }
        catch (Exception exception)
        {
            pending.Completion.TrySetException(exception);
        }
    }

    public void Dispose()
    {
        lock (pendingWindowGate)
        {
            pendingWindowCapture?.Completion.TrySetCanceled();
            pendingWindowCapture = null;
        }
        captureLock.Dispose();
    }

    private async Task<AgentBridgeCaptureReceipt> PersistCaptureAsync(
        IDalamudTextureWrap texture,
        string scope,
        string captureMethod,
        uint? viewportId,
        CancellationToken cancellationToken)
    {
        var pngCodec = readbackProvider.GetSupportedImageEncoderInfos()
            .Single(codec => codec.MimeTypes.Contains("image/png", StringComparer.OrdinalIgnoreCase));
        await using var output = new MemoryStream();
        await readbackProvider.SaveToStreamAsync(
            texture,
            pngCodec.ContainerGuid,
            output,
            new Dictionary<string, object>(),
            leaveWrapOpen: true,
            leaveStreamOpen: true,
            cancellationToken).ConfigureAwait(false);

        var captureId = Guid.NewGuid().ToString("N");
        var fileName = $"{captureId}.bin";
        Directory.CreateDirectory(captureDirectory);
        var path = Path.Combine(captureDirectory, fileName);
        var pngBytes = output.ToArray();
        try
        {
            var protectedBytes = AgentBridgeDataProtection.ProtectBytes(pngBytes, pluginInstanceId);
            try { await File.WriteAllBytesAsync(path, protectedBytes, cancellationToken).ConfigureAwait(false); }
            finally { CryptographicOperations.ZeroMemory(protectedBytes); }
            var receipt = new AgentBridgeCaptureReceipt
            {
                SchemaVersion = 1,
                CaptureId = captureId,
                FileName = fileName,
                CapturedAtUtc = DateTimeOffset.UtcNow,
                Width = texture.Width,
                Height = texture.Height,
                Sha256 = Convert.ToHexString(SHA256.HashData(pngBytes)),
                ProcessId = Environment.ProcessId,
                Scope = scope,
                CaptureMethod = captureMethod,
                ViewportId = viewportId,
            };
            PruneOldCaptures();
            return receipt;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pngBytes);
        }
    }

    internal static uint ResolveCaptureViewportId(
        bool fullViewport,
        uint mainViewportId,
        AgentBridgeCaptureRegion? region)
    {
        var viewportId = fullViewport ? mainViewportId : region?.ViewportId ?? 0;
        return viewportId != 0
            ? viewportId
            : throw new InvalidOperationException("The requested plugin surface has no captureable viewport.");
    }

    private void PruneOldCaptures()
    {
        foreach (var file in new DirectoryInfo(captureDirectory)
                     .EnumerateFiles("*.bin")
                     .OrderByDescending(file => file.LastWriteTimeUtc)
                     .Skip(20))
            file.Delete();
    }

    private sealed record PendingWindowCapture(
        Func<string> WindowName,
        TaskCompletionSource<RenderedWindowCapture> Completion);

    private sealed record RenderedWindowCapture(
        IDrawListTextureWrap Texture,
        uint? ViewportId);
}

public sealed record AgentBridgeCaptureRegion(
    System.Numerics.Vector2 WindowPosition,
    System.Numerics.Vector2 WindowSize,
    uint ViewportId,
    System.Numerics.Vector2 ViewportPosition,
    System.Numerics.Vector2 ViewportSize,
    DateTimeOffset RenderedAtUtc)
{
    private const float PaddingPixels = 8f;

    [Obsolete("Supply the rendered viewport ID. Legacy regions cannot safely identify detached viewport content.")]
    public AgentBridgeCaptureRegion(
        System.Numerics.Vector2 windowPosition,
        System.Numerics.Vector2 windowSize,
        System.Numerics.Vector2 viewportPosition,
        System.Numerics.Vector2 viewportSize,
        DateTimeOffset renderedAtUtc)
        : this(windowPosition, windowSize, 0, viewportPosition, viewportSize, renderedAtUtc)
    {
    }

    [Obsolete("Use the overload that includes the rendered viewport ID.")]
    public void Deconstruct(
        out System.Numerics.Vector2 windowPosition,
        out System.Numerics.Vector2 windowSize,
        out System.Numerics.Vector2 viewportPosition,
        out System.Numerics.Vector2 viewportSize,
        out DateTimeOffset renderedAtUtc)
    {
        windowPosition = WindowPosition;
        windowSize = WindowSize;
        viewportPosition = ViewportPosition;
        viewportSize = ViewportSize;
        renderedAtUtc = RenderedAtUtc;
    }

    public bool IsRecent() => DateTimeOffset.UtcNow - RenderedAtUtc <= TimeSpan.FromSeconds(5);

    public System.Numerics.Vector2 GetUv0() => new(
        Math.Clamp((WindowPosition.X - PaddingPixels - ViewportPosition.X) / ViewportSize.X, 0f, 1f),
        Math.Clamp((WindowPosition.Y - PaddingPixels - ViewportPosition.Y) / ViewportSize.Y, 0f, 1f));

    public System.Numerics.Vector2 GetUv1() => new(
        Math.Clamp((WindowPosition.X + WindowSize.X + PaddingPixels - ViewportPosition.X) / ViewportSize.X, 0f, 1f),
        Math.Clamp((WindowPosition.Y + WindowSize.Y + PaddingPixels - ViewportPosition.Y) / ViewportSize.Y, 0f, 1f));
}
