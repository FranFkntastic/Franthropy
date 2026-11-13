using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using Franthropy.Dalamud.Diagnostics;

namespace Franthropy.Dalamud.Automation.Inventory;

/// <summary>
/// One observation of the serialized inventory boundary. <paramref name="Outcome"/> is
/// <see cref="InventoryMoveOutcome.None"/> on the ordinary bag merge/split path and a
/// self-describing code on the saddlebag retrieval path; <paramref name="Destination"/>
/// is the verified bag stack that received a retrieval.
/// </summary>
public sealed record InventoryMoveObservation(bool Waiting, bool Completed, string Message,
    DalamudInventoryStack? SplitDestination = null, bool Dispatched = false, bool Reconciled = false,
    InventoryMoveOutcome Outcome = InventoryMoveOutcome.None, DalamudInventoryStack? Destination = null);

/// <summary>Distinct outcomes of <see cref="DalamudInventoryMover.BeginSaddlebagRetrieval"/> and its observations.</summary>
public enum InventoryMoveOutcome
{
    /// <summary>Ordinary bag merge/split path; no code is assigned.</summary>
    None,
    /// <summary>Waiting: the executable identity check is still running.</summary>
    ValidatingNative,
    /// <summary>The executable hash, module identity or native bindings are not the researched ones.</summary>
    UnresearchedExecutable,
    /// <summary>The source is not a saddlebag slot or the quantity is outside 1..source quantity.</summary>
    InvalidRequest,
    /// <summary>Waiting: the inventory manager or a player bag is not loaded.</summary>
    InventoryNotLoaded,
    /// <summary>A saddlebag container is not loaded; the client loads it only after the saddlebag has been opened this session.</summary>
    SaddlebagNotLoaded,
    /// <summary>The native movement condition (permission 134) is not granted.</summary>
    MovementNotPermitted,
    /// <summary>The caller's authorization callback refused the move.</summary>
    NotAuthorized,
    /// <summary>The live source slot no longer holds the planned item and quantity.</summary>
    SourceChanged,
    /// <summary>The live source slot holds the planned item and quantity but different HQ, flags, symbolic state or metadata.</summary>
    MetadataMismatch,
    /// <summary>The source is not an ordinary, non-symbolic, stackable item within its authoritative stack size.</summary>
    IncompatibleItem,
    /// <summary>No compatible non-full bag stack with enough room and no empty bag slot.</summary>
    NoBagCapacity,
    /// <summary>A partial quantity needs a native saddlebag split, but no saddlebag slot is empty.</summary>
    NoSaddlebagSplitSpace,
    /// <summary>Waiting: earlier native inventory operations have not retired.</summary>
    NativeQueueBusy,
    /// <summary>Waiting: one operation is in flight; poll <see cref="DalamudInventoryMover.Observe"/>.</summary>
    AwaitingReceipt,
    /// <summary>The native call refused locally; nothing was enqueued and the scope reread unchanged.</summary>
    NotEnqueued,
    /// <summary>The requested quantity left the saddlebag slot and arrived in the reported bag slot.</summary>
    Retrieved,
    /// <summary>
    /// A partial quantity was split into a new saddlebag slot (<see cref="InventoryMoveObservation.SplitDestination"/>).
    /// Nothing reached the bags yet; retrieve that piece as a whole stack next.
    /// </summary>
    Staged,
    /// <summary>The operation retired but other changes invalidated the exact deltas; reread and replan.</summary>
    OutsideMutation,
    /// <summary>The outcome could not be proven (lost/rejected acknowledgement, unproven submission, unconverged repair). Never resent.</summary>
    Indeterminate,
}

/// <summary>
/// Serialized ordinary bag merge/split and saddlebag-to-bag retrieval boundary. Offsets/opcodes are restricted
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
    public static readonly InventoryType[] Saddlebags = [InventoryType.SaddleBag1, InventoryType.SaddleBag2, InventoryType.PremiumSaddleBag1, InventoryType.PremiumSaddleBag2];
    // Existing acknowledgement budget, plus a minimum poll count so a stalled
    // framework cannot turn elapsed wall-clock alone into a loss verdict.
    private static readonly TimeSpan AcknowledgementBudget = TimeSpan.FromSeconds(10);
    // Re-read before classifying a retired operation's delta mismatch.
    private static readonly TimeSpan SettleBudget = TimeSpan.FromSeconds(1);
    private const int MinimumPolls = 10;
    private readonly ISigScanner scanner;
    private readonly IGameInteropProvider interop;
    private readonly IDataManager data;
    private readonly IPluginLog? log;
    private InventoryMoveOutcome poisonOutcome;
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
    // Current-build queued record types (operations exports, 2026.11.03): empty-slot
    // move 0x146 (0x1408738B0), split 0x148 (0x140873C00), merge 0x14A (0x140873AE0).
    internal enum NativeOperation { Move = 0x146, Split = 0x148, Merge = 0x14A }
    private sealed class Pending(uint context, nint manager, DalamudInventoryRead before,
        DalamudInventoryStack source, DalamudInventoryStack? destination, int amount,
        NativeOperation op, IReadOnlyList<InventoryType> scope, bool retrieval)
    {
        public readonly NativeOperation Op = op;
        public readonly IReadOnlyList<InventoryType> Scope = scope;
        public readonly bool Retrieval = retrieval;
        public int Polls;
        public DateTime? SettleStarted;
        public int SettlePolls;
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
        public string Evidence() =>
            $"op={Op} context={Context:X} source={Source.Container}/{Source.SlotIndex} item={Source.ItemId} flags={Source.Flags} " +
            $"sourceQuantity={Source.Quantity} amount={Amount} destination={(Destination == null ? "native-chosen empty slot" : $"{Destination.Container}/{Destination.SlotIndex} quantity={Destination.Quantity}")} " +
            $"ack={Ack} submitted={Submitted} elapsed={(DateTime.UtcNow - Started).TotalMilliseconds:F0}ms polls={Polls}; not retried";
    }

    /// <param name="scanner">Signature scanner for the running executable.</param>
    /// <param name="interop">Hook provider for the acknowledgement observers.</param>
    /// <param name="data">Excel data for authoritative stack sizes.</param>
    /// <param name="log">Optional; receives Warning-level evidence for every saddlebag retrieval failure.</param>
    public DalamudInventoryMover(ISigScanner scanner, IGameInteropProvider interop, IDataManager data, IPluginLog? log = null)
    {
        this.scanner = scanner;
        this.interop = interop;
        this.data = data;
        this.log = log;
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
        *(int*)(record + 8) == (int)p.Op &&
        *(int*)(record + 0x10) == (int)p.Source.Container && *(short*)(record + 0x14) == p.Source.SlotIndex &&
        (p.Op == NativeOperation.Split ? *(int*)(record + 0x18) == p.Amount && *(int*)(record + 0x2C) == p.Amount :
            *(int*)(record + 0x24) == (int)p.Destination!.Container && *(short*)(record + 0x28) == p.Destination.SlotIndex);

    private void EnableObservers()
    {
        hook ??= interop.HookFromAddress<AckDelegate>(validatedAck, OnAck);
        containerHook ??= interop.HookFromAddress<ContainerReceiveDelegate>(validatedContainerReceive, OnContainerReceive);
        resetHook ??= interop.HookFromAddress<ResetDelegate>(validatedReset, OnReset);
        hook.Enable();
        containerHook.Enable();
        resetHook.Enable();
    }

    public InventoryMoveObservation Begin(DalamudInventoryStack source, DalamudInventoryStack? destination,
        int amount, Func<bool> stillAuthorized)
    {
        if (HasOutstanding) return Observe();
        var availability = Preflight();
        if (!availability.Completed) return availability;
        EnableObservers();
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
        return Dispatch(before, Bags, source, destination, destination == null ? NativeOperation.Split : NativeOperation.Merge,
            amount, retrieval: false, stillAuthorized);
    }

    /// <summary>
    /// Begins one verified move of an ordinary stack from a saddlebag slot into the player bags.
    /// Call on the framework thread, then poll <see cref="Observe"/> while the result is waiting.
    /// Available only where the researched executable identity validates.
    /// </summary>
    /// <param name="source">The planned saddlebag stack, exactly as last read (item, quantity, flags and metadata).</param>
    /// <param name="quantity">Units to retrieve: the whole stack, or a proper part of it.</param>
    /// <param name="stillAuthorized">Rechecked immediately before dispatch.</param>
    /// <returns>
    /// One native operation per call. A whole stack (or a part that exactly fills a compatible bag
    /// stack) moves directly and completes as <see cref="InventoryMoveOutcome.Retrieved"/> with the
    /// receiving bag stack in <see cref="InventoryMoveObservation.Destination"/>. Any other partial
    /// quantity is first split natively inside the saddlebag (the client places a saddlebag split only
    /// in a saddlebag slot) and completes as <see cref="InventoryMoveOutcome.Staged"/>; retrieve the
    /// returned <see cref="InventoryMoveObservation.SplitDestination"/> piece as a whole stack next.
    /// Destination: the compatible incomplete bag stack with the least room that still takes the whole
    /// amount (bag then slot order breaks ties), otherwise the first empty bag slot. Exactly full stacks
    /// never merge, because the native merge swaps them. An unproven outcome is reported as
    /// <see cref="InventoryMoveOutcome.Indeterminate"/> and never resent.
    /// </returns>
    public InventoryMoveObservation BeginSaddlebagRetrieval(DalamudInventoryStack source, int quantity, Func<bool> stillAuthorized)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(stillAuthorized);
        if (HasOutstanding) return Observe();
        var availability = Preflight();
        if (availability.Waiting) return availability with { Outcome = InventoryMoveOutcome.ValidatingNative };
        if (!availability.Completed) return Refuse(InventoryMoveOutcome.UnresearchedExecutable, availability.Message, source, quantity);
        if (!Saddlebags.Contains(source.Container) || quantity <= 0 || quantity > source.Quantity)
            return Refuse(InventoryMoveOutcome.InvalidRequest, "Source must be a saddlebag slot and quantity within 1..source quantity.", source, quantity);
        EnableObservers();
        if (!CanMove) return Refuse(InventoryMoveOutcome.MovementNotPermitted, "Native movement permission 134 is not granted.", source, quantity);
        if (!stillAuthorized()) return Refuse(InventoryMoveOutcome.NotAuthorized, "Caller authorization refused the move.", source, quantity);
        var scope = SaddlebagScope(source, out var notLoaded);
        if (scope == null)
            return notLoaded is { } missing && Saddlebags.Contains(missing)
                ? Refuse(InventoryMoveOutcome.SaddlebagNotLoaded, $"Saddlebag container {missing} is not loaded; the client loads it only after the saddlebag is opened this session.", source, quantity)
                : new(true, false, $"Waiting for inventory container {notLoaded?.ToString() ?? "manager"} to load.", Outcome: InventoryMoveOutcome.InventoryNotLoaded);
        var before = DalamudInventoryStackScanner.ReadComplete(scope);
        if (!before.IsComplete) return new(true, false, before.Message, Outcome: InventoryMoveOutcome.InventoryNotLoaded);
        var capacity = data.GetExcelSheet<Lumina.Excel.Sheets.Item>().TryGetRow(source.ItemId, out var row) ? (int)row.StackSize : 0;
        var plan = PlanSaddlebagRetrieval(before.Stacks, source, quantity, capacity);
        if (plan.Refusal != InventoryMoveOutcome.None) return Refuse(plan.Refusal, plan.Message, source, quantity);
        return Dispatch(before, scope, source, plan.Destination, plan.Operation, plan.Amount, retrieval: true, stillAuthorized);
    }

    internal sealed record SaddlebagRetrievalPlan(InventoryMoveOutcome Refusal, string Message,
        NativeOperation Operation = NativeOperation.Move, DalamudInventoryStack? Destination = null, int Amount = 0);

    /// <summary>Pure destination selection over a complete bag + saddlebag read.</summary>
    internal static SaddlebagRetrievalPlan PlanSaddlebagRetrieval(IReadOnlyList<DalamudInventoryStack> stacks,
        DalamudInventoryStack source, int quantity, int capacity)
    {
        var live = stacks.SingleOrDefault(x => x.Container == source.Container && x.SlotIndex == source.SlotIndex);
        if (live == null || live.ItemId != source.ItemId || live.Quantity != source.Quantity)
            return new(InventoryMoveOutcome.SourceChanged, $"Live slot holds item={live?.ItemId} quantity={live?.Quantity}.");
        if (live != source)
            return new(InventoryMoveOutcome.MetadataMismatch, $"Live slot flags={live.Flags} symbolic={live.IsSymbolic} metadataChanged={live.Metadata != source.Metadata}; planned flags={source.Flags} symbolic={source.IsSymbolic}.");
        if (!source.IsOrdinary || source.ItemId <= 19 || capacity <= 1 || source.Quantity > capacity)
            return new(InventoryMoveOutcome.IncompatibleItem, $"Not an ordinary stackable item (flags={source.Flags}, symbolic={source.IsSymbolic}, stackSize={capacity}).");
        var bags = stacks.Where(x => Bags.Contains(x.Container)).ToArray();
        var empties = bags.Where(x => x.ItemId == 0 && x.Quantity == 0 && !x.IsSymbolic).ToArray();
        // Native merge of an exactly full stack (either side) swaps instead, so
        // only incomplete stacks on both sides are merge candidates.
        var mergeable = source.Quantity >= capacity ? [] : bags
            .Where(x => x.CompatibleWith(source) && x.Quantity < capacity)
            .OrderBy(x => capacity - x.Quantity).ToArray();
        var fits = mergeable.FirstOrDefault(x => capacity - x.Quantity >= quantity);
        var capacityEvidence = $"stackSize={capacity} compatibleIncompleteBagStacks={mergeable.Length} " +
            $"maxRoom={(mergeable.Length == 0 ? 0 : capacity - mergeable[^1].Quantity)} emptyBagSlots={empties.Length}.";
        if (quantity == source.Quantity)
        {
            if (fits != null) return new(InventoryMoveOutcome.None, "Whole-stack merge.", NativeOperation.Merge, fits, quantity);
            if (empties.Length != 0) return new(InventoryMoveOutcome.None, "Whole-stack move to empty bag slot.", NativeOperation.Move, empties[0], quantity);
            return new(InventoryMoveOutcome.NoBagCapacity, capacityEvidence);
        }
        // Capacity-limited merge transfers min(S, C-D): exact only when C-D equals the request.
        var exact = mergeable.FirstOrDefault(x => capacity - x.Quantity == quantity);
        if (exact != null) return new(InventoryMoveOutcome.None, "Capacity-limited exact merge.", NativeOperation.Merge, exact, quantity);
        if (fits == null && empties.Length == 0) return new(InventoryMoveOutcome.NoBagCapacity, capacityEvidence);
        // Native SplitItem from a saddlebag searches only saddlebag containers for the new slot.
        if (!stacks.Any(x => Saddlebags.Contains(x.Container) && x.ItemId == 0 && x.Quantity == 0 && !x.IsSymbolic))
            return new(InventoryMoveOutcome.NoSaddlebagSplitSpace, "Partial retrieval needs an empty saddlebag slot for the native split.");
        return new(InventoryMoveOutcome.None, "Stage partial quantity by saddlebag split.", NativeOperation.Split, null, quantity);
    }

    private static InventoryType[]? SaddlebagScope(DalamudInventoryStack source, out InventoryType? notLoaded)
    {
        notLoaded = null;
        var manager = InventoryManager.Instance();
        if (manager == null) return null;
        foreach (var type in Bags.Concat(Saddlebags.Take(2)))
            if (!Loaded(manager, type)) { notLoaded = type; return null; }
        // Premium pages exist only with the subscription; include them when the
        // client has them (a native split may land there) or the source is there.
        var premium = Loaded(manager, Saddlebags[2]) || Loaded(manager, Saddlebags[3]) || Array.IndexOf(Saddlebags, source.Container) >= 2;
        if (!premium) return [.. Bags, .. Saddlebags.Take(2)];
        foreach (var type in Saddlebags.Skip(2))
            if (!Loaded(manager, type)) { notLoaded = type; return null; }
        return [.. Bags, .. Saddlebags];

        static bool Loaded(InventoryManager* m, InventoryType type)
        {
            var c = m->GetInventoryContainer(type);
            return c != null && c->IsLoaded && c->Size > 0;
        }
    }

    private InventoryMoveObservation Refuse(InventoryMoveOutcome outcome, string message, DalamudInventoryStack source, int quantity)
    {
        var text = $"{outcome}: {message} source={source.Container}/{source.SlotIndex} item={source.ItemId} flags={source.Flags} quantity={source.Quantity} requested={quantity}; nothing dispatched.";
        log?.Warning("{InventoryMoveRefusal}", text);
        return new(false, false, text, Outcome: outcome);
    }

    private static InventoryMoveOutcome Code(Pending p, InventoryMoveOutcome outcome) => p.Retrieval ? outcome : InventoryMoveOutcome.None;

    private InventoryMoveObservation Dispatch(DalamudInventoryRead before, IReadOnlyList<InventoryType> scope, DalamudInventoryStack source,
        DalamudInventoryStack? destination, NativeOperation op, int amount, bool retrieval, Func<bool> stillAuthorized)
    {
        var manager = (byte*)InventoryManager.Instance();
        if (manager == null) return new(true, false, "Inventory manager unavailable.", Outcome: retrieval ? InventoryMoveOutcome.InventoryNotLoaded : InventoryMoveOutcome.None);
        // Require a quiet operation queue, stronger than merely one free entry:
        // this prevents another outstanding stock operation sharing our slots.
        for (var i = 0; i < 128; i++)
            if (manager[i * 0x3C] == 0)
                return new(true, false, "Waiting for earlier native inventory operations to retire.", Outcome: retrieval ? InventoryMoveOutcome.NativeQueueBusy : InventoryMoveOutcome.None);
        var context = *(uint*)(manager + 0x1E00);
        var p = new Pending(context, (nint)manager, before, source, destination, amount, op, scope, retrieval);
        lock (gate) pending = p;
        if (!stillAuthorized() || !CanMove)
        {
            lock (gate) pending = null;
            return retrieval
                ? Refuse(CanMove ? InventoryMoveOutcome.NotAuthorized : InventoryMoveOutcome.MovementNotPermitted, "Authorization or movement permission changed immediately before dispatch.", source, amount)
                : new(true, false, "Inventory movement ownership changed.");
        }
        var result = op == NativeOperation.Split
            ? ((InventoryManager*)manager)->SplitItem(source.Container, checked((ushort)source.SlotIndex), amount)
            : ((InventoryManager*)manager)->MoveItemSlot(source.Container, checked((ushort)source.SlotIndex), destination!.Container, checked((ushort)destination.SlotIndex), true);
        var record = FindRecord(manager, context);
        p.Submitted = result == 0 && record != null && *(uint*)(manager + 0x1E00) == ((context + 1) % 0x0FFFFFFF | 0x10000000) &&
            *(int*)(record + 8) == (int)op &&
            *(int*)(record + 0x10) == (int)source.Container && *(short*)(record + 0x14) == source.SlotIndex;
        if (p.Submitted && destination != null)
            p.Submitted = *(int*)(record + 0x24) == (int)destination.Container && *(short*)(record + 0x28) == destination.SlotIndex;
        if (p.Submitted && op == NativeOperation.Split)
            p.Submitted = *(int*)(record + 0x18) == amount && *(int*)(record + 0x2C) == amount &&
                before.Stacks.Any(x => (int)x.Container == *(int*)(record + 0x24) && x.SlotIndex == *(short*)(record + 0x28) && x.ItemId == 0 && x.Quantity == 0);
        // A receive on another thread can retire the record before this call
        // returns. Its fully correlated hook observation still proves enqueue.
        lock (gate)
            p.Submitted |= result == 0 && p.Ack is >= 0 and < 256 &&
                *(uint*)(manager + 0x1E00) == ((context + 1) % 0x0FFFFFFF | 0x10000000);
        if (!p.Submitted)
        {
            var unchanged = DalamudInventoryStackScanner.ReadComplete(scope);
            if (result != 0 && record == null && *(uint*)(manager + 0x1E00) == context &&
                unchanged.IsComplete && before.Stacks.SequenceEqual(unchanged.Stacks))
            {
                lock (gate) pending = null;
                hook!.Disable();
                containerHook!.Disable();
                resetHook!.Disable();
                var text = $"Nothing enqueued (return {result}); unchanged inventory reread, discard old plan.";
                if (retrieval)
                {
                    // Saddlebag moves/splits pass extra native availability gates that
                    // refuse (return 10/13) before any local mutation or enqueue.
                    text = $"{InventoryMoveOutcome.NotEnqueued}: {text} {p.Evidence()}";
                    log?.Warning("{InventoryMoveRefusal}", text);
                }
                return new(false, false, text, Reconciled: true, Outcome: Code(p, InventoryMoveOutcome.NotEnqueued));
            }
            poison = $"Inventory submission not established (return {result}, context {context:X}); no replay is permitted.";
            poisonOutcome = Code(p, InventoryMoveOutcome.Indeterminate);
            if (retrieval) log?.Warning("{InventoryMoveFailure}", $"{InventoryMoveOutcome.Indeterminate}: {poison} {p.Evidence()}");
        }
        return new(true, false, $"Inventory {op.ToString().ToLowerInvariant()} context={context:X}, item={source.ItemId}, amount={amount}, submitted={p.Submitted}; awaiting accepted acknowledgement.",
            Dispatched: true, Outcome: Code(p, InventoryMoveOutcome.AwaitingReceipt));
    }

    public InventoryMoveObservation Observe()
    {
        Pending? p;
        lock (gate) p = pending;
        if (p == null) return poison == null ? new(false, true, "No outstanding inventory operation.") : new(false, false, poison, Outcome: poisonOutcome);
        p.Polls++;
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
            return DateTime.UtcNow - p.Started > AcknowledgementBudget && p.Polls >= MinimumPolls
                ? Reconcile(p, $"Inventory context {p.Context:X} acknowledgement lost; no replay.")
                : new(true, false, $"Waiting for inventory acknowledgement/retirement context={p.Context:X}.", Outcome: Code(p, InventoryMoveOutcome.AwaitingReceipt));
        var after = DalamudInventoryStackScanner.ReadComplete(p.Scope);
        if (!after.IsComplete) return new(true, false, after.Message, Outcome: Code(p, InventoryMoveOutcome.AwaitingReceipt));
        var destination = ExactDeltaDestination(p, after);
        if (destination == null)
        {
            // Settle before classifying: re-read for a bounded window and poll count.
            p.SettleStarted ??= DateTime.UtcNow;
            if (++p.SettlePolls < MinimumPolls || DateTime.UtcNow - p.SettleStarted < SettleBudget)
                return new(true, false, $"Settling inventory deltas for context={p.Context:X}.", Outcome: Code(p, InventoryMoveOutcome.AwaitingReceipt));
            return ReconcileOutsideMutation(p, after);
        }
        lock (gate) pending = null;
        poison = null;
        hook?.Disable();
        containerHook?.Disable();
        resetHook?.Disable();
        var arrived = after.Stacks.Single(x => x.Container == destination.Container && x.SlotIndex == destination.SlotIndex);
        if (p.Retrieval)
        {
            var staged = p.Op == NativeOperation.Split;
            var receipt = staged
                ? $"{InventoryMoveOutcome.Staged}: accepted context={p.Context:X}; {p.Amount} split inside the saddlebag to {arrived.Container}/{arrived.SlotIndex}; retrieve that piece next. {p.Evidence()}"
                : $"{InventoryMoveOutcome.Retrieved}: accepted context={p.Context:X}; {p.Amount} left {p.Source.Container}/{p.Source.SlotIndex} and arrived in {arrived.Container}/{arrived.SlotIndex} (now {arrived.Quantity}); every other scoped slot unchanged. {p.Evidence()}";
            log?.Information("{InventoryMoveReceipt}", receipt);
            return new(false, true, receipt, staged ? arrived : null,
                Outcome: staged ? InventoryMoveOutcome.Staged : InventoryMoveOutcome.Retrieved, Destination: staged ? null : arrived);
        }
        return new(false, true, $"Accepted inventory context={p.Context:X}; retired and conserved exact bag deltas verified.",
            p.Destination == null ? arrived : null);
    }

    // The verified destination slot when every scoped slot shows exactly the
    // expected source/destination deltas, otherwise null.
    private static DalamudInventoryStack? ExactDeltaDestination(Pending p, DalamudInventoryRead after)
    {
        var destination = p.Destination;
        if (destination == null)
        {
            var candidates = after.Stacks.Where(x => x.Quantity == p.Amount && x.CompatibleWith(p.Source) &&
                p.Before.Stacks.Any(b => b.Container == x.Container && b.SlotIndex == x.SlotIndex && b.ItemId == 0 && b.Quantity == 0)).ToArray();
            if (candidates.Length != 1) return null;
            destination = candidates[0] with { Quantity = 0 };
        }
        foreach (var b in p.Before.Stacks)
        {
            var a = after.Stacks.SingleOrDefault(x => x.Container == b.Container && x.SlotIndex == b.SlotIndex);
            var expected = b.Quantity;
            var identity = b;
            if (b.Container == p.Source.Container && b.SlotIndex == p.Source.SlotIndex) expected -= p.Amount;
            if (b.Container == destination.Container && b.SlotIndex == destination.SlotIndex)
            { expected += p.Amount; identity = p.Op == NativeOperation.Merge ? b : p.Source; }
            if (a == null || a.Quantity != expected || (expected == 0 ? a.ItemId != 0 || a.IsSymbolic :
                a.ItemId != identity.ItemId || a.Flags != identity.Flags || a.IsSymbolic != identity.IsSymbolic || a.Metadata != identity.Metadata))
                return null;
        }
        return destination;
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
        var text = $"Accepted context retired, but outside mutation invalidated exact deltas (totals conserved={conserved}). Complete inventory reread; discard every dependent move and replan.";
        if (p.Retrieval)
        {
            text = $"{InventoryMoveOutcome.OutsideMutation}: {text} {p.Evidence()}";
            log?.Warning("{InventoryMoveFailure}", text);
        }
        return new(false, false, text, Reconciled: true, Outcome: Code(p, InventoryMoveOutcome.OutsideMutation));
    }

    private InventoryMoveObservation Fail(Pending p, string message)
    {
        if (p.Retrieval && poison != message)
            log?.Warning("{InventoryMoveFailure}", $"{InventoryMoveOutcome.Indeterminate}: {message} {p.Evidence()}");
        poison = message;
        poisonOutcome = Code(p, InventoryMoveOutcome.Indeterminate);
        return new(false, false, message, Outcome: poisonOutcome);
    }

    private InventoryMoveObservation Reconcile(Pending p, string reason)
    {
        var manager = (byte*)InventoryManager.Instance();
        if (manager == null) return new(true, false, "Waiting for authoritative inventory manager to recover.", Outcome: Code(p, InventoryMoveOutcome.AwaitingReceipt));
        // Retirement, not elapsed time, permits refresh. An actually in-flight
        // operation remains held even after timeout; never erase its native record.
        for (var i = 0; i < 128; i++)
            if (manager[i * 0x3C] == 0)
            {
                // An unrelated operation temporarily delays the quiet-queue
                // gate; it must not destroy the last repair round's correlation
                // after the bounded request budget is exhausted. Native reset
                // and differing received contexts invalidate credits separately.
                return DateTime.UtcNow - p.Started > AcknowledgementBudget
                    ? Fail(p, "Native inventory operation still active; queue intent retained, no replay. Recovery resumes after native retirement.")
                    : new(true, false, "Waiting for native operations to retire before inventory repair.", Outcome: Code(p, InventoryMoveOutcome.AwaitingReceipt));
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
                    var text = "All four native container refresh contexts received/applied after retirement; discard dependent moves and build a fresh plan.";
                    if (p.Retrieval)
                    {
                        // Only the bag refresh request is researched; the saddlebag side
                        // was not authoritatively refreshed, so the move stays unproven.
                        text = $"{InventoryMoveOutcome.Indeterminate}: {reason} {text} Saddlebag containers were not authoritatively refreshed. {p.Evidence()}";
                        log?.Warning("{InventoryMoveFailure}", text);
                    }
                    return new(false, false, text, Reconciled: true, Outcome: Code(p, InventoryMoveOutcome.Indeterminate));
                }
            }
            if (p.RefreshContexts.Count != 0 && DateTime.UtcNow - p.RepairStarted < TimeSpan.FromSeconds(10))
                return new(true, false, $"Reconciling inventory containers ({p.Refreshed.Count}/4 authoritative responses).", Outcome: Code(p, InventoryMoveOutcome.AwaitingReceipt));
            if (p.RepairAttempts >= 3)
                return Fail(p, "Inventory resync did not converge after three native refresh rounds; queue intent retained. Late correlated responses can still complete repair; no replay permitted.");
            if (!DalamudInventoryStackScanner.ReadComplete(Bags).IsComplete)
                return new(true, false, "Waiting for all four bags before requesting authoritative repair.", Outcome: Code(p, InventoryMoveOutcome.AwaitingReceipt));
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
                { p.RefreshContexts.Clear(); return Fail(p, "Native inventory refresh context did not advance; repair remains held."); }
            }
        }
        return new(true, false, reason + " Requesting authoritative bag resync; no movement replay.", Outcome: Code(p, InventoryMoveOutcome.AwaitingReceipt));
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
