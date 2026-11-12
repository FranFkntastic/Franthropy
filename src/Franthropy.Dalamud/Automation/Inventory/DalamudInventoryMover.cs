using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using Franthropy.Dalamud.Diagnostics;

namespace Franthropy.Dalamud.Automation.Inventory;

public sealed record InventoryMoveObservation(bool Waiting, bool Completed, string Message,
    DalamudInventoryStack? SplitDestination = null, bool Dispatched = false, bool Reconciled = false);

/// <summary>
/// Serialized ordinary bag merge/split boundary. Offsets/opcodes are restricted
/// to the researched executable, not inferred from an SDK layout or return code.
/// Call only on the framework thread; the receive hook only records acknowledgement.
/// </summary>
public sealed unsafe class DalamudInventoryMover : IDisposable
{
    public const string ResearchedClientSha256 = "CEB4260B2574476ACF72E7B6105D0A4F8D1BD78B4E7CAB5D8115C453F2F39339";
    public const string ResearchedClientVersion = "2026.11.03.0000.0000";
    public const string ResearchedClientStructs = "433e87a565258a4174764aa24cf5405ac404ccca";
    private const string AckSignature = "4C 8B DC 55 41 54 49 8D 6B A1 48 81 EC 98 00 00 00 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 45 27 49 89 5B 18 45 33 E4 49 89 7B E8";
    public static readonly InventoryType[] Bags = [InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4];
    private readonly ISigScanner scanner;
    private readonly IGameInteropProvider interop;
    private readonly IDataManager data;
    private Hook<AckDelegate>? hook;
    private Hook<ContainerReceiveDelegate>? containerHook;
    private Hook<ResetDelegate>? resetHook;
    private nint validatedReset;
    private ContainerRequestDelegate? requestContainer;
    private nint validatedContainerReceive;
    private const string ContainerRequestSignature = "48 89 5C 24 10 48 89 6C 24 18 48 89 7C 24 20 41 56 48 83 EC 50 45 33 F6 41 8B E8 8B FA 48 8B D9 81 FA DD 07 00 00";
    private const string ContainerReceiveSignature = "4C 8B DC 53 56 57 41 55 41 57 48 83 EC 40 45 33 FF 48 8D 05 ?? ?? ?? ?? 49 89 43 B8 48 8B F9 45 8B E8 48 8B DA BE 0F 27 00 00";
    private const string ResetSignature = "48 89 5C 24 08 48 89 74 24 10 48 89 7C 24 18 55 48 8B EC 48 83 EC 60 BA 08 00 00 00 48 8D 41 78 45 33 C0 44 8D 4A F7";
    private readonly object gate = new();
    private Pending? pending;
    private string? poison;
    private bool disposed;
    private Task<string?>? hashCheck;
    private string? availabilityError;
    private nint validatedAck;
    private string? binaryPath;
    private long binaryLength;
    private DateTime binaryWriteTime;
    private nint moduleBase;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void AckDelegate(nint manager, byte* payload);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ContainerRequestDelegate(nint manager, int inventory, int reason);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ContainerReceiveDelegate(nint manager, byte* chain, uint target);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ResetDelegate(nint manager);
    private sealed class Pending(uint context, nint manager, DalamudInventoryRead before,
        DalamudInventoryStack source, DalamudInventoryStack? destination, int amount)
    {
        public readonly uint Context = context;
        public readonly nint Manager = manager;
        public readonly DalamudInventoryRead Before = before;
        public readonly DalamudInventoryStack Source = source;
        public readonly DalamudInventoryStack? Destination = destination;
        public readonly int Amount = amount;
        public readonly DateTime Started = DateTime.UtcNow;
        public int Ack = -1;
        public bool OverlappingUpdate;
        public bool Submitted;
        public nint RepairManager;
        public readonly Dictionary<int, uint> RefreshContexts = new();
        public readonly HashSet<int> Refreshed = new();
        public DateTime RepairStarted;
        public int RepairAttempts;
        public int RepairEpoch;
        public bool Reset;
    }

    public DalamudInventoryMover(ISigScanner scanner, IGameInteropProvider interop, IDataManager data)
    {
        this.scanner = scanner;
        this.interop = interop;
        this.data = data;
    }

    public bool HasOutstanding { get { lock (gate) return pending != null; } }
    public static bool CanMove => Conditions.Instance() != null && Conditions.Instance()->HasPermission(134);

    // Lazy: constructing an automation service neither installs hooks nor touches native memory.
    public InventoryMoveObservation Preflight()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var file = new FileInfo(scanner.Module.FileName);
        if (hashCheck == null)
        {
            binaryPath = file.FullName;
            binaryLength = file.Length;
            binaryWriteTime = file.LastWriteTimeUtc;
            moduleBase = scanner.Module.BaseAddress;
            var path = binaryPath;
            hashCheck = Task.Run(() => HashBinary(path));
        }
        if (binaryPath != file.FullName || binaryLength != file.Length || binaryWriteTime != file.LastWriteTimeUtc || moduleBase != scanner.Module.BaseAddress)
            availabilityError = "Inventory binary/module identity changed after validation; movement is unavailable.";
        if (!hashCheck.IsCompleted) return new(true, false, "Validating inventory native availability...");
        availabilityError ??= hashCheck.GetAwaiter().GetResult();
        if (availabilityError != null) return new(false, false, availabilityError);
        if (validatedAck != 0)
        {
            if ((nint)InventoryManager.MemberFunctionPointers.MoveItemSlot != moduleBase + 0x86E180 ||
                (nint)InventoryManager.MemberFunctionPointers.SplitItem != moduleBase + 0x870380)
                return new(false, false, availabilityError = "Inventory SDK bindings changed after validation.");
            return new(false, true, "Inventory native availability validated; no observer installed.");
        }
        try { ValidateBindings(); }
        catch (Exception ex) { availabilityError = ex.Message; }
        return new(false, availabilityError == null, availabilityError ?? "Inventory native availability validated; no observer installed.");
    }

    private static string? HashBinary(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream)) == ResearchedClientSha256 ? null :
                "Inventory operation layout is not researched for this executable hash.";
        }
        catch (Exception ex) { return $"Inventory executable validation failed: {ex.Message}"; }
    }

    private void ValidateBindings()
    {
        var ack = NativeCapabilityGuard.ResolveUnique(scanner, AckSignature, "Inventory operation acknowledgement");
        NativeCapabilityGuard.RequireIdentity(ack, scanner.Module.BaseAddress + 0x872B70, "Inventory acknowledgement");
        var move = NativeCapabilityGuard.ResolveUnique(scanner, "E8 ?? ?? ?? ?? 48 8B 03 66 FF C5", "Inventory move callsite");
        move += 5 + *(int*)(move + 1);
        NativeCapabilityGuard.RequireIdentity(move, (nint)InventoryManager.MemberFunctionPointers.MoveItemSlot, "Inventory move SDK binding");
        NativeCapabilityGuard.RequireIdentity(move, scanner.Module.BaseAddress + 0x86E180, "Inventory move identity");
        var split = NativeCapabilityGuard.ResolveUnique(scanner, "40 55 53 56 57 41 55 41 57 48 8D 6C 24 ?? 48 81 EC ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 45 ?? 8D B2", "Inventory split");
        NativeCapabilityGuard.RequireIdentity(split, (nint)InventoryManager.MemberFunctionPointers.SplitItem, "Inventory split SDK binding");
        NativeCapabilityGuard.RequireIdentity(split, scanner.Module.BaseAddress + 0x870380, "Inventory split identity");
        var permission = NativeCapabilityGuard.ResolveUnique(scanner, "E8 ?? ?? ?? ?? 84 C0 75 ?? 8B FB", "Inventory permission callsite");
        permission += 5 + *(int*)(permission + 1);
        NativeCapabilityGuard.RequireIdentity(permission, (nint)Conditions.MemberFunctionPointers.HasPermission, "Inventory permission SDK binding");
        var request = NativeCapabilityGuard.ResolveUnique(scanner, ContainerRequestSignature, "Inventory container refresh request");
        NativeCapabilityGuard.RequireIdentity(request, moduleBase + 0x86D630, "Inventory container request identity");
        validatedContainerReceive = NativeCapabilityGuard.ResolveUnique(scanner, ContainerReceiveSignature, "Inventory full container receive");
        NativeCapabilityGuard.RequireIdentity(validatedContainerReceive, moduleBase + 0x86D800, "Inventory container receive identity");
        requestContainer = Marshal.GetDelegateForFunctionPointer<ContainerRequestDelegate>(request);
        validatedReset = NativeCapabilityGuard.ResolveUnique(scanner, ResetSignature, "Inventory pending-operation reset");
        NativeCapabilityGuard.RequireIdentity(validatedReset, moduleBase + 0x86BE80, "Inventory reset identity");
        validatedAck = ack;
    }

    private void OnReset(nint manager)
    {
        try
        {
            lock (gate)
                if (pending is { } p && (p.Manager == manager || p.RepairManager == manager))
                {
                    p.Reset = true;
                    p.RepairEpoch++;
                    p.RepairAttempts = 0;
                    p.RefreshContexts.Clear();
                    p.Refreshed.Clear();
                }
        }
        catch { /* Reset still belongs to the client. */ }
        try { resetHook!.Original(manager); }
        catch { /* Nothing managed may escape the detour. */ }
    }

    private void OnContainerReceive(nint manager, byte* chain, uint target)
    {
        Pending? observed = null;
        var inventory = -1;
        uint context = 0;
        var epoch = 0;
        try
        {
            // Native assembled receive chain: 0x198 header followed by the full
            // container contents. Never credit individual optimistic slot writes.
            var nodes = 0;
            for (var node = chain; node != null && nodes++ < 512; node = *(byte**)(node + 0x68))
            {
                if (*(int*)(node + 8) != 0x198) continue;
                inventory = *(int*)(node + 0x18);
                context = *(uint*)(node + 0x10);
                lock (gate)
                    if (pending is { } p && p.RepairManager == manager &&
                        p.RefreshContexts.ContainsKey(inventory))
                    { observed = p; epoch = p.RepairEpoch; }
                break;
            }
        }
        catch { /* Native handler must still run. */ }
        var applied = false;
        try { containerHook!.Original(manager, chain, target); applied = true; }
        catch { /* Never credit an incomplete receive. */ }
        try
        {
            lock (gate)
                if (applied && observed != null && ReferenceEquals(pending, observed) &&
                    observed.RepairManager == manager && observed.RepairEpoch == epoch)
                {
                    if (observed.RefreshContexts.GetValueOrDefault(inventory) == context)
                        observed.Refreshed.Add(inventory);
                    else
                        observed.Refreshed.Remove(inventory); // A different generation superseded the credited response.
                }
        }
        catch { /* Nothing managed may escape the detour. */ }
    }

    private void OnAck(nint manager, byte* payload)
    {
        // Observer failures must never suppress the game's receive handler or
        // escape across its unmanaged boundary. Publish only after Original.
        Pending? observed = null;
        var result = -1;
        var refresh = false;
        try
        {
            lock (gate)
            {
                var p = pending;
                if (p != null && payload != null && p.Manager == manager && *(uint*)payload == p.Context)
                {
                    var record = FindRecord((byte*)manager, p.Context);
                    if (record != null)
                    {
                        observed = p;
                        refresh = record[0x34] != 0 || record[0x35] != 0 || record[0x36] != 0 || record[0x37] != 0;
                        result = MatchesRecord(record, p) ? payload[5] : 256;
                    }
                }
            }
        }
        catch { /* The native handler still owns this event. */ }
        finally
        {
            try { hook!.Original(manager, payload); }
            catch { result = 256; } // Nothing managed may cross this detour.
        }
        try
        {
            lock (gate)
                if (observed != null && ReferenceEquals(pending, observed))
                {
                    observed.OverlappingUpdate |= refresh;
                    observed.Ack = observed.Ack == -1 ? result : 256;
                }
        }
        catch { /* Never propagate a managed observation failure. */ }
    }

    private static byte* FindRecord(byte* manager, uint context)
    {
        for (var i = 0; i < 128; i++)
        {
            var record = manager + i * 0x3C;
            if (record[0] == 0 && *(uint*)(record + 4) == context) return record;
        }
        return null;
    }

    private static bool MatchesRecord(byte* record, Pending p) =>
        *(int*)(record + 8) == (p.Destination == null ? 0x148 : 0x14A) &&
        *(int*)(record + 0x10) == (int)p.Source.Container && *(short*)(record + 0x14) == p.Source.SlotIndex &&
        (p.Destination == null ? *(int*)(record + 0x18) == p.Amount && *(int*)(record + 0x2C) == p.Amount :
            *(int*)(record + 0x24) == (int)p.Destination.Container && *(short*)(record + 0x28) == p.Destination.SlotIndex);

    public InventoryMoveObservation Begin(DalamudInventoryStack source, DalamudInventoryStack? destination,
        int amount, Func<bool> stillAuthorized)
    {
        if (HasOutstanding) return Observe();
        var availability = Preflight();
        if (!availability.Completed) return availability;
        hook ??= interop.HookFromAddress<AckDelegate>(validatedAck, OnAck);
        containerHook ??= interop.HookFromAddress<ContainerReceiveDelegate>(validatedContainerReceive, OnContainerReceive);
        resetHook ??= interop.HookFromAddress<ResetDelegate>(validatedReset, OnReset);
        hook.Enable();
        containerHook.Enable();
        resetHook.Enable();
        if (!CanMove || !stillAuthorized()) return new(true, false, "Waiting for inventory movement permission and ownership.");
        var before = DalamudInventoryStackScanner.ReadComplete(Bags);
        if (!before.IsComplete) return new(true, false, before.Message);
        if (!before.Stacks.Contains(source) || !source.IsOrdinary || source.ItemId <= 19 || amount <= 0)
            return new(false, false, "Inventory source changed; discard the plan and reread.");
        var capacity = checked((int)(data.GetExcelSheet<Lumina.Excel.Sheets.Item>().GetRow(source.ItemId).StackSize));
        if (capacity <= 1 || source.Quantity > capacity) return new(false, false, "Source is not an ordinary stackable material within authoritative capacity.");
        if (destination == null)
        {
            if (amount >= source.Quantity || !before.Stacks.Any(x => x.ItemId == 0 && x.Quantity == 0 && !x.IsSymbolic))
                return new(false, false, "Exact split requires a proper partial quantity and an empty bag slot.");
        }
        else if (!before.Stacks.Contains(destination) || !source.CompatibleWith(destination) ||
            source.Container == destination.Container && source.SlotIndex == destination.SlotIndex ||
            source.Quantity >= capacity || destination.Quantity >= capacity ||
            amount != Math.Min(source.Quantity, capacity - destination.Quantity))
            return new(false, false, "Merge no longer satisfies exact ordinary incomplete-stack capacity guards.");
        var manager = (byte*)InventoryManager.Instance();
        if (manager == null) return new(true, false, "Inventory manager unavailable.");
        // Require a quiet operation queue, stronger than merely one free entry:
        // this prevents another outstanding stock operation sharing our slots.
        for (var i = 0; i < 128; i++)
            if (manager[i * 0x3C] == 0) return new(true, false, "Waiting for earlier native inventory operations to retire.");
        var context = *(uint*)(manager + 0x1E00);
        var p = new Pending(context, (nint)manager, before, source, destination, amount);
        lock (gate) pending = p;
        if (!stillAuthorized() || !CanMove) { lock (gate) pending = null; return new(true, false, "Inventory movement ownership changed."); }
        var result = destination == null
            ? ((InventoryManager*)manager)->SplitItem(source.Container, checked((ushort)source.SlotIndex), amount)
            : ((InventoryManager*)manager)->MoveItemSlot(source.Container, checked((ushort)source.SlotIndex), destination.Container, checked((ushort)destination.SlotIndex), true);
        var record = FindRecord(manager, context);
        p.Submitted = result == 0 && record != null && *(uint*)(manager + 0x1E00) == ((context + 1) % 0x0FFFFFFF | 0x10000000) &&
            *(int*)(record + 8) == (destination == null ? 0x148 : 0x14A) &&
            *(int*)(record + 0x10) == (int)source.Container && *(short*)(record + 0x14) == source.SlotIndex;
        if (p.Submitted && destination != null)
            p.Submitted = *(int*)(record + 0x24) == (int)destination.Container && *(short*)(record + 0x28) == destination.SlotIndex;
        if (p.Submitted && destination == null)
            p.Submitted = *(int*)(record + 0x18) == amount && *(int*)(record + 0x2C) == amount &&
                before.Stacks.Any(x => (int)x.Container == *(int*)(record + 0x24) && x.SlotIndex == *(short*)(record + 0x28) && x.ItemId == 0 && x.Quantity == 0);
        // A receive on another thread can retire the record before this call
        // returns. Its fully correlated hook observation still proves enqueue.
        lock (gate)
            p.Submitted |= result == 0 && p.Ack is >= 0 and < 256 &&
                *(uint*)(manager + 0x1E00) == ((context + 1) % 0x0FFFFFFF | 0x10000000);
        if (!p.Submitted)
        {
            var unchanged = DalamudInventoryStackScanner.ReadComplete(Bags);
            if (result != 0 && record == null && *(uint*)(manager + 0x1E00) == context &&
                unchanged.IsComplete && before.Stacks.SequenceEqual(unchanged.Stacks))
            {
                lock (gate) pending = null;
                hook.Disable();
                containerHook.Disable();
                resetHook.Disable();
                return new(false, false, $"Nothing enqueued (return {result}); unchanged inventory reread, discard old plan.", Reconciled: true);
            }
            poison = $"Inventory submission not established (return {result}, context {context:X}); no replay is permitted.";
        }
        return new(true, false, $"Inventory {(destination == null ? "split" : "merge")} context={context:X}, item={source.ItemId}, amount={amount}, submitted={p.Submitted}; awaiting accepted acknowledgement.", Dispatched: true);
    }

    public InventoryMoveObservation Observe()
    {
        Pending? p;
        lock (gate) p = pending;
        if (p == null) return poison == null ? new(false, true, "No outstanding inventory operation.") : new(false, false, poison);
        if ((nint)InventoryManager.Instance() != p.Manager)
            return Reconcile(p, "Inventory manager changed; old operation/plan invalidated.");
        int ack;
        bool refresh;
        lock (gate) { ack = p.Ack; refresh = p.OverlappingUpdate; }
        var activeRecord = FindRecord((byte*)p.Manager, p.Context);
        if (activeRecord != null && !MatchesRecord(activeRecord, p))
            return Reconcile(p, "Inventory context collided; discard dependent plan.");
        if (p.Reset || p.RepairAttempts != 0 || !p.Submitted || ack > 0 || refresh)
            return Reconcile(p, $"Inventory context {p.Context:X} requires authoritative repair (ack={ack}, overlap={refresh}).");
        if (ack < 0 || FindRecord((byte*)p.Manager, p.Context) != null)
            return DateTime.UtcNow - p.Started > TimeSpan.FromSeconds(10)
                ? Reconcile(p, $"Inventory context {p.Context:X} acknowledgement lost; no replay.")
                : new(true, false, $"Waiting for inventory acknowledgement/retirement context={p.Context:X}.");
        var after = DalamudInventoryStackScanner.ReadComplete(Bags);
        if (!after.IsComplete) return new(true, false, after.Message);
        var destination = p.Destination;
        if (destination == null)
        {
            var candidates = after.Stacks.Where(x => x.Quantity == p.Amount && x.CompatibleWith(p.Source) &&
                p.Before.Stacks.Any(b => b.Container == x.Container && b.SlotIndex == x.SlotIndex && b.ItemId == 0 && b.Quantity == 0)).ToArray();
            if (candidates.Length != 1) return ReconcileOutsideMutation(p, after);
            destination = candidates[0] with { Quantity = 0 };
        }
        foreach (var b in p.Before.Stacks)
        {
            var a = after.Stacks.SingleOrDefault(x => x.Container == b.Container && x.SlotIndex == b.SlotIndex);
            var expected = b.Quantity;
            var identity = b;
            if (b.Container == p.Source.Container && b.SlotIndex == p.Source.SlotIndex) expected -= p.Amount;
            if (b.Container == destination.Container && b.SlotIndex == destination.SlotIndex)
            { expected += p.Amount; identity = p.Destination == null ? p.Source : b; }
            if (a == null || a.Quantity != expected || (expected == 0 ? a.ItemId != 0 || a.IsSymbolic :
                a.ItemId != identity.ItemId || a.Flags != identity.Flags || a.IsSymbolic != identity.IsSymbolic || a.Metadata != identity.Metadata))
                return ReconcileOutsideMutation(p, after);
        }
        lock (gate) pending = null;
        poison = null;
        hook?.Disable();
        containerHook?.Disable();
        resetHook?.Disable();
        return new(false, true, $"Accepted inventory context={p.Context:X}; retired and conserved exact bag deltas verified.",
            p.Destination == null ? after.Stacks.Single(x => x.Container == destination.Container && x.SlotIndex == destination.SlotIndex) : null);
    }

    private InventoryMoveObservation ReconcileOutsideMutation(Pending p, DalamudInventoryRead after)
    {
        // Accepted + retired resolves the sent operation, but not its old plan.
        // Reread the complete inventory for a NEW plan even if another operation
        // changed totals; never issue the split's dependent merge from a guess.
        static string Totals(DalamudInventoryRead read) => string.Join(";", read.Stacks.Where(x => x.ItemId != 0)
            .GroupBy(x => (x.ItemId, x.Flags, x.IsSymbolic, x.Metadata))
            .Select(g => $"{g.Key}:{g.Sum(x => (long)x.Quantity)}").Order(StringComparer.Ordinal));
        var conserved = Totals(p.Before) == Totals(after);
        lock (gate) pending = null;
        poison = null;
        hook?.Disable();
        containerHook?.Disable();
        resetHook?.Disable();
        return new(false, false, $"Accepted context retired, but outside mutation invalidated exact deltas (totals conserved={conserved}). Complete inventory reread; discard every dependent move and replan.", Reconciled: true);
    }

    private InventoryMoveObservation Fail(string message) { poison = message; return new(false, false, message); }

    private InventoryMoveObservation Reconcile(Pending p, string reason)
    {
        var manager = (byte*)InventoryManager.Instance();
        if (manager == null) return new(true, false, "Waiting for authoritative inventory manager to recover.");
        // Retirement, not elapsed time, permits refresh. An actually in-flight
        // operation remains held even after timeout; never erase its native record.
        for (var i = 0; i < 128; i++)
            if (manager[i * 0x3C] == 0)
            {
                // An unrelated operation temporarily delays the quiet-queue
                // gate; it must not destroy the last repair round's correlation
                // after the bounded request budget is exhausted. Native reset
                // and differing received contexts invalidate credits separately.
                return DateTime.UtcNow - p.Started > TimeSpan.FromSeconds(10)
                    ? Fail("Native inventory operation still active; queue intent retained, no replay. Recovery resumes after native retirement.")
                    : new(true, false, "Waiting for native operations to retire before inventory repair.");
            }
        lock (gate)
        {
            if (p.RepairManager != (nint)manager)
            { p.RepairManager = (nint)manager; p.RefreshContexts.Clear(); p.Refreshed.Clear(); p.RepairAttempts = 0; }
            if (p.RefreshContexts.Count == 4 && p.Refreshed.Count == 4)
            {
                var read = DalamudInventoryStackScanner.ReadComplete(Bags);
                if (read.IsComplete)
                {
                    pending = null;
                    poison = null;
                    hook?.Disable();
                    containerHook?.Disable();
                    resetHook?.Disable();
                    return new(false, false, "All four native container refresh contexts received/applied after retirement; discard dependent moves and build a fresh plan.", Reconciled: true);
                }
            }
            if (p.RefreshContexts.Count != 0 && DateTime.UtcNow - p.RepairStarted < TimeSpan.FromSeconds(10))
                return new(true, false, $"Reconciling inventory containers ({p.Refreshed.Count}/4 authoritative responses).");
            if (p.RepairAttempts >= 3)
                return Fail("Inventory resync did not converge after three native refresh rounds; queue intent retained. Late correlated responses can still complete repair; no replay permitted.");
            if (!DalamudInventoryStackScanner.ReadComplete(Bags).IsComplete)
                return new(true, false, "Waiting for all four bags before requesting authoritative repair.");
            p.RefreshContexts.Clear();
            p.Refreshed.Clear();
            p.RepairStarted = DateTime.UtcNow;
            p.RepairAttempts++;
            p.RepairEpoch++;
            foreach (var bag in Bags)
            {
                var context = *(uint*)(manager + 0x1E00);
                p.RefreshContexts[(int)bag] = context;
                // Same native helper used by acknowledgement correction. It
                // owns sending and advancing the request context; no raw packet.
                requestContainer!((nint)manager, (int)bag, 0);
                if (*(uint*)(manager + 0x1E00) != ((context + 1) % 0x0FFFFFFF | 0x10000000))
                { p.RefreshContexts.Clear(); return Fail("Native inventory refresh context did not advance; repair remains held."); }
            }
        }
        return new(true, false, reason + " Requesting authoritative bag resync; no movement replay.");
    }

    public void Suspend()
    {
        // Keep only the observer needed to retain a late outstanding ack. No
        // framework subscription, dependent dispatch or replay runs while idle.
        if (!HasOutstanding) { hook?.Disable(); containerHook?.Disable(); resetHook?.Disable(); }
    }

    public void Dispose()
    {
        disposed = true;
        hook?.Dispose();
        containerHook?.Dispose();
        resetHook?.Dispose();
        resetHook = null;
        containerHook = null;
        hook = null;
        // No attempt to undo/replay an outstanding server operation on unload.
    }
}
