using System.Diagnostics;
using System.Runtime.InteropServices;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using Franthropy.Dalamud.Diagnostics;

namespace Franthropy.Dalamud.Observations;

/// <summary>Read-only observers: each original is called exactly once, unchanged.</summary>
internal sealed unsafe class RetainerVisitProofHooks : IDisposable
{
    internal const string SelectionSignature = "48 89 91 E0 02 00 00 C3";
    internal const string ApplySignature = "48 89 5C 24 ?? 57 48 83 EC 20 48 8B DA 48 8B F9 E8 ?? ?? ?? ?? 48 85 DB 74 ?? 66 0F 1F 44 00 00 81 7B 08 ?? ?? 00 00 75 ?? 48 0F BF 43 18 83 F8 14";
    private delegate void SelectRetainer(nint manager, ulong id);
    private delegate void ApplyBatch(nint manager, nint node);
    private Hook<SelectRetainer>? selection;
    private Hook<ApplyBatch>? apply;
    private uint priceOpcode;
    private readonly Action<ulong, ulong, double> opened;
    private readonly Action<int[], double> applied;
    private readonly Action<string, Exception?> report;
    internal bool Available { get; private set; }
    internal static double Now => Stopwatch.GetTimestamp() * 1000d / Stopwatch.Frequency;

    internal RetainerVisitProofHooks(IGameInteropProvider? interop, ISigScanner? scanner,
        Action<ulong, ulong, double> opened, Action<int[], double> applied, Action<string, Exception?> report)
    {
        this.opened = opened;
        this.applied = applied;
        this.report = report;
        var name = "SetActiveRetainer";
        int? count = null;
        try
        {
            if (interop is null || scanner is null)
                throw new NativeCapabilityUnavailableException("Interop provider and signature scanner are required.");
            var selectAddress = Resolve(SelectionSignature);
            selection = interop.HookFromAddress<SelectRetainer>(selectAddress, SelectDetour);
            name = "ApplyRetainerMarketPriceBatch";
            count = null;
            var applyAddress = Resolve(ApplySignature);
            priceOpcode = checked((uint)Marshal.ReadInt32(applyAddress + 0x23));
            apply = interop.HookFromAddress<ApplyBatch>(applyAddress, ApplyDetour);
            name = "SetActiveRetainer";
            selection.Enable();
            name = "ApplyRetainerMarketPriceBatch";
            apply.Enable();
            Available = true;

            nint Resolve(string signature)
            {
                var matches = scanner.ScanAllText(signature);
                count = matches.Length;
                var address = NativeCapabilityGuard.ResolveUnique(matches, name);
                NativeCapabilityGuard.RequireExecutableAddress(scanner, address, name);
                return address;
            }
        }
        catch (Exception ex)
        {
            try { Dispose(); }
            catch (Exception cleanup) { ex = new AggregateException(ex, cleanup); }
            Report($"[Warning] RetainerVisitHooksUnavailable: hook={name}, signatureMatches={count?.ToString() ?? "unknown"}; all passive retainer captures refused for this collector session.", ex);
        }
    }

    private void SelectDetour(nint manager, ulong id)
    {
        try { if (Available) opened(*(ulong*)(manager + 0x2E0), id, Now); }
        catch (Exception ex) { Fail(ex); }
        selection!.Original(manager, id);
    }

    private void ApplyDetour(nint manager, nint node)
    {
        try
        {
            if (Available)
            {
                var slots = new HashSet<int>();
                var seen = new HashSet<nint>();
                for (var current = node; current != 0; current = *(nint*)(current + 0x68))
                {
                    if (seen.Count >= 4096 || !seen.Add(current))
                        throw new InvalidDataException("Price batch list is cyclic or excessive.");
                    if (*(uint*)(current + 8) != priceOpcode) continue;
                    var slot = *(short*)(current + 0x18);
                    if (slot is < 0 or >= 20) throw new InvalidDataException($"Invalid price slot {slot}.");
                    slots.Add(slot);
                }
                // Revoke proof BEFORE Original: it can synchronously emit inventory/UI events.
                // Publication is deferred to the framework tick after native application.
                applied(slots.Order().ToArray(), Now);
            }
        }
        catch (Exception ex) { Fail(ex); }
        apply!.Original(manager, node);
    }

    private void Fail(Exception ex)
    {
        Available = false;
        Report("[Warning] RetainerVisitHooksUnavailable: observation failed; all passive retainer captures refused for this collector session.", ex);
    }

    private void Report(string message, Exception? ex)
    {
        try { report(message, ex); } catch { /* Never unwind through a native detour. */ }
    }

    public void Dispose()
    {
        Available = false;
        try { apply?.Dispose(); }
        finally { selection?.Dispose(); }
    }
}
