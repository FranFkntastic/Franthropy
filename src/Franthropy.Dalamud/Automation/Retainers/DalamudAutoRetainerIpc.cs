using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

namespace Franthropy.Dalamud.Automation.Retainers;

public sealed record AutoRetainerIpcCallbacks(
    Action DrawRetainerListTaskButtons,
    Action<string> RetainerAdditionalTask,
    Action<string, string> RetainerReadyForPostprocess);

/// <summary>
/// Product-neutral AutoRetainer IPC transport. Consumers retain ownership of
/// refresh policy, UI, game interaction, and captured data.
/// </summary>
public interface IAutoRetainerIpc : IDisposable
{
    bool IsAvailable { get; }
    bool IsBusy { get; }
    bool IsSuppressed { get; }
    void Register(AutoRetainerIpcCallbacks callbacks);
    void QueueRetainerListTask(string consumer);
    void RequestPostprocess(string consumer);
    void FinishPostprocess();
    void SetSuppressed(bool suppressed);
}

public sealed class DalamudAutoRetainerIpc : IAutoRetainerIpc
{
    private const string InitChannel = "AutoRetainer.Init";
    private const string IsBusyChannel = "AutoRetainer.PluginState.IsBusy";
    private const string DrawButtonsChannel = "AutoRetainer.OnRetainerListTaskButtonsDraw";
    private const string CustomTaskChannel = "AutoRetainer.OnRetainerListCustomTask";
    private const string AdditionalTaskChannel = "AutoRetainer.OnRetainerAdditionalTask";
    private const string RequestPostprocessChannel = "AutoRetainer.RequestPostprocess";
    private const string ReadyForPostprocessChannel = "AutoRetainer.OnRetainerReadyForPostprocess";
    private const string FinishPostprocessChannel = "AutoRetainer.FinishPostprocessRequest";
    private const string GetSuppressedChannel = "AutoRetainer.GetSuppressed";
    private const string SetSuppressedChannel = "AutoRetainer.SetSuppressed";

    private readonly ICallGateSubscriber<object> init;
    private readonly ICallGateSubscriber<bool> isBusy;
    private readonly ICallGateSubscriber<object> drawButtons;
    private readonly ICallGateSubscriber<string, object> customTask;
    private readonly ICallGateSubscriber<string, object> additionalTask;
    private readonly ICallGateSubscriber<string, object> requestPostprocess;
    private readonly ICallGateSubscriber<string, string, object> readyForPostprocess;
    private readonly ICallGateSubscriber<object> finishPostprocess;
    private readonly ICallGateSubscriber<bool> getSuppressed;
    private readonly ICallGateSubscriber<bool, object> setSuppressed;
    private AutoRetainerIpcCallbacks? callbacks;
    private bool registered;
    private bool disposed;

    public DalamudAutoRetainerIpc(IDalamudPluginInterface pluginInterface)
        : this(
            pluginInterface.GetIpcSubscriber<object>(InitChannel),
            pluginInterface.GetIpcSubscriber<bool>(IsBusyChannel),
            pluginInterface.GetIpcSubscriber<object>(DrawButtonsChannel),
            pluginInterface.GetIpcSubscriber<string, object>(CustomTaskChannel),
            pluginInterface.GetIpcSubscriber<string, object>(AdditionalTaskChannel),
            pluginInterface.GetIpcSubscriber<string, object>(RequestPostprocessChannel),
            pluginInterface.GetIpcSubscriber<string, string, object>(ReadyForPostprocessChannel),
            pluginInterface.GetIpcSubscriber<object>(FinishPostprocessChannel),
            pluginInterface.GetIpcSubscriber<bool>(GetSuppressedChannel),
            pluginInterface.GetIpcSubscriber<bool, object>(SetSuppressedChannel))
    {
    }

    internal DalamudAutoRetainerIpc(
        ICallGateSubscriber<object> init,
        ICallGateSubscriber<bool> isBusy,
        ICallGateSubscriber<object> drawButtons,
        ICallGateSubscriber<string, object> customTask,
        ICallGateSubscriber<string, object> additionalTask,
        ICallGateSubscriber<string, object> requestPostprocess,
        ICallGateSubscriber<string, string, object> readyForPostprocess,
        ICallGateSubscriber<object> finishPostprocess,
        ICallGateSubscriber<bool> getSuppressed,
        ICallGateSubscriber<bool, object> setSuppressed)
    {
        this.init = init;
        this.isBusy = isBusy;
        this.drawButtons = drawButtons;
        this.customTask = customTask;
        this.additionalTask = additionalTask;
        this.requestPostprocess = requestPostprocess;
        this.readyForPostprocess = readyForPostprocess;
        this.finishPostprocess = finishPostprocess;
        this.getSuppressed = getSuppressed;
        this.setSuppressed = setSuppressed;
    }

    public bool IsAvailable
    {
        get
        {
            if (disposed)
                return false;
            try
            {
                init.InvokeAction();
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    public bool IsBusy
    {
        get
        {
            if (disposed)
                return false;
            try
            {
                return isBusy.InvokeFunc();
            }
            catch
            {
                return false;
            }
        }
    }

    public bool IsSuppressed
    {
        get
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return getSuppressed.InvokeFunc();
        }
    }

    public void Register(AutoRetainerIpcCallbacks callbacks)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(callbacks);
        if (registered)
            return;

        this.callbacks = callbacks;
        var drawRegistered = false;
        var additionalRegistered = false;
        var readyRegistered = false;
        try
        {
            drawButtons.Subscribe(OnDrawButtons);
            drawRegistered = true;
            additionalTask.Subscribe(OnAdditionalTask);
            additionalRegistered = true;
            readyForPostprocess.Subscribe(OnReadyForPostprocess);
            readyRegistered = true;
            registered = true;
        }
        catch
        {
            if (readyRegistered)
                TryUnsubscribe(() => readyForPostprocess.Unsubscribe(OnReadyForPostprocess));
            if (additionalRegistered)
                TryUnsubscribe(() => additionalTask.Unsubscribe(OnAdditionalTask));
            if (drawRegistered)
                TryUnsubscribe(() => drawButtons.Unsubscribe(OnDrawButtons));
            this.callbacks = null;
            throw;
        }
    }

    public void QueueRetainerListTask(string consumer)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        customTask.InvokeAction(RequireConsumer(consumer));
    }

    public void RequestPostprocess(string consumer)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        requestPostprocess.InvokeAction(RequireConsumer(consumer));
    }

    public void FinishPostprocess()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        finishPostprocess.InvokeAction();
    }

    public void SetSuppressed(bool suppressed)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        setSuppressed.InvokeAction(suppressed);
    }

    private void OnDrawButtons() => callbacks?.DrawRetainerListTaskButtons();
    private void OnAdditionalTask(string retainerName) => callbacks?.RetainerAdditionalTask(retainerName);
    private void OnReadyForPostprocess(string consumer, string retainerName) => callbacks?.RetainerReadyForPostprocess(consumer, retainerName);

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        callbacks = null;
        if (registered)
        {
            TryUnsubscribe(() => readyForPostprocess.Unsubscribe(OnReadyForPostprocess));
            TryUnsubscribe(() => additionalTask.Unsubscribe(OnAdditionalTask));
            TryUnsubscribe(() => drawButtons.Unsubscribe(OnDrawButtons));
            registered = false;
        }
    }

    private static string RequireConsumer(string consumer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(consumer);
        return consumer;
    }

    private static void TryUnsubscribe(Action unsubscribe)
    {
        try
        {
            unsubscribe();
        }
        catch
        {
            // Disposal and registration rollback must attempt every subscription.
        }
    }
}
