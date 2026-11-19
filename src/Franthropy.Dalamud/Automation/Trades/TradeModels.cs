using System;
using System.Collections.Generic;
using System.Linq;

namespace Franthropy.Dalamud.Automation.Trades;

[Serializable]
public sealed class TradeItem
{
    public uint ItemId { get; set; }
    public string ItemName { get; set; } = string.Empty;
    public bool IsHighQuality { get; set; }
    public int Quantity { get; set; }
}

public sealed record TradeInventoryStack(
    uint ContainerId,
    int SlotIndex,
    uint ItemId,
    string ItemName,
    bool IsHighQuality,
    int Quantity);

public sealed record TradeInventoryObservation(
    bool IsAuthoritative,
    IReadOnlyList<TradeInventoryStack> Stacks)
{
    public static TradeInventoryObservation Unavailable { get; } = new(false, []);

    public static TradeInventoryObservation Authoritative(
        IReadOnlyList<TradeInventoryStack> stacks) => new(true, stacks);
}

public sealed record TradeBatchLine(
    uint ContainerId,
    int SlotIndex,
    uint ItemId,
    string ItemName,
    bool IsHighQuality,
    int Quantity,
    int SourceStackQuantity);

public sealed record TradeBatch(
    IReadOnlyList<TradeBatchLine> Lines,
    int GilAmount,
    IReadOnlyDictionary<TradeInventoryKey, int> ExpectedInventoryBefore)
{
    public int SlotCount => Lines.Count;
    public int ItemUnitCount => Lines.Sum(line => line.Quantity);
    public int UnitCount => checked(ItemUnitCount + GilAmount);
}

public readonly record struct TradeItemKey(uint ItemId, bool IsHighQuality = false);

public readonly record struct TradeInventoryKey(uint ItemId, bool IsHighQuality);

public sealed record TradePartner(
    ulong GameObjectId,
    string Name,
    uint HomeWorldId,
    string HomeWorldName = "")
{
    internal bool Matches(ulong objectId, string name, uint homeWorldId) =>
        GameObjectId == objectId && Name == name && HomeWorldId == homeWorldId;
}

[Serializable]
public sealed class TradeTimingOptions
{
    public const int DefaultActionDelayMilliseconds = 200;
    public const int DefaultTradeRetryMilliseconds = 1_000;
    public const int MinimumActionDelayMilliseconds = 50;
    public const int MaximumActionDelayMilliseconds = 1_000;
    public const int MinimumTradeRetryMilliseconds = 1_000;
    public const int MaximumTradeRetryMilliseconds = 5_000;

    public int ActionDelayMilliseconds { get; set; } = DefaultActionDelayMilliseconds;
    public int TradeRetryMilliseconds { get; set; } = DefaultTradeRetryMilliseconds;

    public TimeSpan ActionDelay => TimeSpan.FromMilliseconds(
        Math.Clamp(
            ActionDelayMilliseconds,
            MinimumActionDelayMilliseconds,
            MaximumActionDelayMilliseconds));

    public TimeSpan TradeRetryDelay => TimeSpan.FromMilliseconds(
        Math.Clamp(
            TradeRetryMilliseconds,
            MinimumTradeRetryMilliseconds,
            MaximumTradeRetryMilliseconds));
}

[Serializable]
public sealed class TradeRecoveryState
{
    public ulong OwnerContentId { get; set; }
    public TradeConfirmation? AwaitingVerification { get; set; }
    public bool ObserveOnly { get; set; }
    public bool CancellationPending { get; set; }
    public uint PartnerEntityId { get; set; }
}

// List-shaped baseline so ordinary JSON stores need no custom dictionary-key converter.
public sealed record TradeConfirmation(ulong Sender, TradePartner Partner, IReadOnlyList<TradeBatchLine> Lines,
    int GilAmount, IReadOnlyList<TradeOfferedItem> Before)
{
    internal TradeBatch ToBatch() => new(Lines, GilAmount,
        Before.ToDictionary(x => new TradeInventoryKey(x.ItemId, x.IsHighQuality), x => x.Quantity));
}

public enum TradeSettlement { Delivered, NotDelivered }
public sealed record TradeStartResult(bool Success, string Message, string? Code = null);

public enum TradeValidationCode
{
    Ready,
    Empty,
    InvalidQuantity,
    InsufficientInventory,
}

public sealed record TradeValidationResult(
    bool Success,
    TradeValidationCode Code,
    string Message);

public enum TradeExecutionState
{
    Idle,
    PreparingInventory,
    OpeningTrade,
    OfferingItems,
    WaitingForPartner,
    ConfirmingTrade,
    VerifyingInventory,
    Completed,
    Stopped,
    Failed,
    ClosingTrade,
}

public sealed record TradeExecutionSnapshot(
    TradeExecutionState State,
    string Message,
    string? RunId,
    string? PartnerName,
    int BatchNumber,
    int BatchSlotCount,
    int CompletedBatchCount,
    int InitialUnitCount,
    int CompletedUnitCount,
    int RemainingItemCount,
    int RemainingUnitCount,
    bool IsActive);
