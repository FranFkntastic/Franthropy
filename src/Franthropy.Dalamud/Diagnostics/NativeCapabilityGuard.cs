using System.Runtime.InteropServices;
using Dalamud.Plugin.Services;

namespace Franthropy.Dalamud.Diagnostics;

/// <summary>Checks actual native prerequisites without a game-build allowlist.</summary>
public static class NativeCapabilityGuard
{
    public const string FailureCode = "NativeCapabilityUnavailable";

    public static void RequireAddress(nint address, string name)
    {
        if (address == 0)
            throw new NativeCapabilityUnavailableException($"{name} native address could not be resolved.");
    }

    public static nint ResolveUnique(ISigScanner scanner, string signature, string name)
    {
        ArgumentNullException.ThrowIfNull(scanner);
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new NativeCapabilityUnavailableException($"{name} requires the Windows x64 native calling convention.");
        var address = ResolveUnique(scanner.ScanAllText(signature), name);
        var start = scanner.Module.BaseAddress + checked((nint)scanner.TextSectionOffset);
        if (address < start || address - start >= scanner.TextSectionSize)
            throw new NativeCapabilityUnavailableException($"{name} resolved outside the game's executable code section.");
        return address;
    }

    public static nint ResolveUnique(IReadOnlyList<nint> matches, string name)
    {
        if (matches.Count != 1 || matches[0] == 0)
            throw new NativeCapabilityUnavailableException($"{name} signature resolved {matches.Count} matches; exactly one nonzero address is required.");
        return matches[0];
    }

    public static void RequireEmbeddedFieldLayout(int ownerSize, int offset, int fieldSize, string name)
    {
        if (ownerSize <= 0 || offset < 0 || fieldSize <= 0 || offset > ownerSize - fieldSize)
            throw new NativeCapabilityUnavailableException($"{name} is outside its enclosing native allocation; the layout is unsupported.");
    }
}

public sealed class NativeCapabilityUnavailableException(string message) : InvalidOperationException(message) { }
