using Dalamud.Bindings.ImGui;

namespace Franthropy.Dalamud.AgentBridge;

/// <summary>
/// Dispatches leased input after platform input collection but before ImGui consumes
/// its event queue. Register from a UI Draw callback; never dispatch from Draw itself.
/// </summary>
public sealed unsafe class ReflectedPluginWindowInputFramePump : IDisposable
{
    private readonly ReflectedPluginWindowInputController controller;
    private readonly Action<Exception> diagnostic;
    // Native hooks retain function pointers, not managed delegate roots.
    private readonly ImGuiContextHookCallback callback;
    private ImGuiContextPtr context;
    private uint frameHook;
    private uint shutdownHook;
    private bool disposed;

    public ReflectedPluginWindowInputFramePump(
        ReflectedPluginWindowInputController controller, Action<Exception> diagnostic)
    {
        this.controller = controller ?? throw new ArgumentNullException(nameof(controller));
        this.diagnostic = diagnostic ?? throw new ArgumentNullException(nameof(diagnostic));
        callback = OnContextHook;
    }

    /// <summary>Registers once on the current UI context, or refuses context drift.</summary>
    public void EnsureRegistered()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var current = ImGui.GetCurrentContext();
        if (current.IsNull)
            throw new InvalidOperationException("Reflected input requires a current ImGui context.");
        if (frameHook != 0)
        {
            if (current.Handle != context.Handle)
                throw new InvalidOperationException("The reflected input ImGui context changed.");
            return;
        }
        context = current;
        try
        {
            var frame = new ImGuiContextHook(type: ImGuiContextHookType.NewFramePre, callback: callback);
            frameHook = ImGuiP.AddContextHook(context, ref frame);
            var shutdown = new ImGuiContextHook(type: ImGuiContextHookType.Shutdown, callback: callback);
            shutdownHook = ImGuiP.AddContextHook(context, ref shutdown);
            if (frameHook == 0 || shutdownHook == 0)
                throw new InvalidOperationException("ImGui refused reflected input frame hooks.");
        }
        catch
        {
            RemoveHooks();
            throw;
        }
    }

    private void OnContextHook(ImGuiContext* callbackContext, ImGuiContextHook* hook)
    {
        // Never allow a managed exception to unwind across the native callback.
        try
        {
            if (disposed || callbackContext != context.Handle)
                return;
            if (hook->Type == ImGuiContextHookType.Shutdown)
            {
                frameHook = shutdownHook = 0;
                context = default;
                disposed = true;
                controller.Dispose();
                return;
            }
            controller.RenderFrame();
        }
        catch (Exception exception)
        {
            try { diagnostic(exception); }
            catch { /* A diagnostic must not escape a native callback either. */ }
        }
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        RemoveHooks();
        controller.Dispose();
        GC.KeepAlive(callback);
    }

    private void RemoveHooks()
    {
        if (!context.IsNull)
        {
            if (frameHook != 0) ImGuiP.RemoveContextHook(context, frameHook);
            if (shutdownHook != 0) ImGuiP.RemoveContextHook(context, shutdownHook);
        }
        frameHook = shutdownHook = 0;
        context = default;
    }
}
