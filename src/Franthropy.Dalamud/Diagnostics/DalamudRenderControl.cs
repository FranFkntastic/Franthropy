using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;

namespace Franthropy.Dalamud.Diagnostics;

/// <summary>
/// Direct reader/writer for the client's active-render flag at
/// <c>Render.Manager.Instance() + 0x38358</c>. This cell is client-owned: the write persists
/// locally and only suppresses local 3D rendering, which makes it safe to use for reducing GPU
/// load on background clients. The offset is a hardcoded render-manager layout constant and must
/// be re-verified after any FFXIVClientStructs render-manager layout change. The flag is restored
/// on dispose when, and only when, this service was the one that cleared it.
/// </summary>
public sealed unsafe class DalamudRenderControl : IDisposable
{
    private static readonly IntPtr ActiveRenderFlagOffset = new(0x38358);
    private const string ApprovedGameVersion = "2026.08.05.0000.0000";
    private const string PatchContractId = "franthropy.render-manager-active-flag";

    private readonly IPluginLog log;
    private bool disabledByThisService;
    private bool disposed;

    public DalamudRenderControl(IPluginLog log)
    {
        this.log = log;
    }

    public bool ReadRenderEnabled()
    {
        EnsureNotDisposed();
        return ReadRenderEnabledRaw();
    }

    public RenderControlDiagnostics GetDiagnostics()
    {
        EnsureNotDisposed();
        return CreateDiagnostics();
    }

    public void SetRenderEnabled(bool enabled)
    {
        EnsureNotDisposed();

        var before = ReadRenderEnabledRaw();
        SetRenderEnabledRaw(enabled);
        var after = ReadRenderEnabledRaw();

        if (!enabled && before)
            disabledByThisService = true;
        else if (enabled)
            disabledByThisService = false;

        log.Information(
            "[Franthropy] 3D render flag write: before={Before} requested={Requested} after={After}",
            before,
            enabled,
            after);
    }

    public bool ToggleRender()
    {
        var enabled = ReadRenderEnabled();
        SetRenderEnabled(!enabled);
        return !enabled;
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;

        if (!disabledByThisService)
            return;

        try
        {
            if (!TryGetRenderFlagPointer(out _, out var flagPointer, out var error))
            {
                log.Warning("[Franthropy] Skipped 3D render restore during dispose: {Error}", error);
                return;
            }

            *flagPointer = 1;
            disabledByThisService = false;
            log.Information("[Franthropy] Restored 3D render flag during dispose.");
        }
        catch (Exception ex)
        {
            log.Error(ex, "[Franthropy] Failed to restore 3D render flag during dispose.");
        }
    }

    private static bool ReadRenderEnabledRaw()
    {
        if (!TryGetRenderFlagPointer(out _, out var flagPointer, out var error))
            throw CreateUnavailableException(error);

        return *flagPointer != 0;
    }

    private static void SetRenderEnabledRaw(bool enabled)
    {
        if (!TryGetRenderFlagPointer(out _, out var flagPointer, out var error))
            throw CreateUnavailableException(error);

        *flagPointer = enabled ? (byte)1 : (byte)0;
    }

    private static bool TryGetRenderFlagPointer(out Manager* manager, out byte* flagPointer, out string error)
    {
        manager = null;
        flagPointer = null;
        error = string.Empty;

        var compatibility = GamePatchCompatibilityGate.Evaluate(PatchContractId, ApprovedGameVersion);
        if (!compatibility.IsApproved)
        {
            error = compatibility.Message;
            return false;
        }

        try
        {
            manager = Manager.Instance();
        }
        catch (Exception ex)
        {
            error = $"render manager unavailable: Render.Manager.Instance() threw {ex.GetType().Name}: {ex.Message}";
            return false;
        }

        if (manager == null)
        {
            error = "render manager unavailable: Render.Manager.Instance() returned null.";
            return false;
        }

        flagPointer = (byte*)manager + ActiveRenderFlagOffset.ToInt32();
        return true;
    }

    private static RenderControlUnavailableException CreateUnavailableException(string error)
    {
        var diagnostics = CreateDiagnostics();
        var message = diagnostics.Error ?? error;

        return new RenderControlUnavailableException(
            $"{message} ClientStructs={diagnostics.ClientStructsAssemblyPath}; " +
            $"ClientStructsVersion={diagnostics.ClientStructsAssemblyVersion}; Manager={diagnostics.ManagerAddressText}; " +
            $"Flag={diagnostics.RenderFlagAddressText}; Byte={diagnostics.CurrentByteText}.");
    }

    private static RenderControlDiagnostics CreateDiagnostics()
    {
        var clientStructsAssembly = typeof(Manager).Assembly;

        Manager* manager = null;
        byte* flagPointer = null;
        byte? currentByte = null;
        string? error = null;

        if (TryGetRenderFlagPointer(out manager, out flagPointer, out var pointerError))
            currentByte = *flagPointer;
        else
            error = pointerError;

        return new RenderControlDiagnostics(
            clientStructsAssembly.Location,
            clientStructsAssembly.GetName().Version?.ToString() ?? "(unknown)",
            (nint)(void*)manager,
            (nint)flagPointer,
            currentByte,
            error);
    }

    private void EnsureNotDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }
}

public sealed record RenderControlDiagnostics(
    string ClientStructsAssemblyPath,
    string ClientStructsAssemblyVersion,
    nint ManagerAddress,
    nint RenderFlagAddress,
    byte? CurrentByte,
    string? Error)
{
    public string ManagerAddressText => FormatAddress(ManagerAddress);
    public string RenderFlagAddressText => FormatAddress(RenderFlagAddress);
    public string CurrentByteText => CurrentByte.HasValue ? $"{CurrentByte.Value} (0x{CurrentByte.Value:X2})" : "(unavailable)";

    private static string FormatAddress(nint address)
    {
        if (address == nint.Zero)
            return "0x0";

        if (IntPtr.Size == 8)
            return $"0x{unchecked((ulong)address.ToInt64()):X}";

        return $"0x{unchecked((uint)address.ToInt32()):X}";
    }
}

public sealed class RenderControlUnavailableException : InvalidOperationException
{
    public RenderControlUnavailableException(string message)
        : base(message)
    {
    }
}
