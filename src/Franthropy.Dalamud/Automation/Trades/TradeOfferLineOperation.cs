using System;
using System.Linq;

namespace Franthropy.Dalamud.Automation.Trades;

internal enum TradeOfferLineState
{
    ReadyToOffer,
    WaitingForQuantityInput,
    AwaitingSubmitClosure,
    WaitingForSlot,
    Completed,
    Failed,
}

internal sealed record TradeOfferLineResult(
    TradeOfferLineState State,
    string Message)
{
    public bool IsCompleted => State == TradeOfferLineState.Completed;
    public bool IsFailed => State == TradeOfferLineState.Failed;
}

// Owns one exact, already-planned inventory line. The runner sees only its outcome;
// raw slot visibility and the transient numeric dialog never imply completion alone.
internal sealed class TradeOfferLineOperation
{
    private readonly ITradeIo io;
    private readonly TradeBatchLine line;
    private readonly int expectedQuantityAfterOffer;
    private readonly DateTimeOffset deadline;
    private TradeOfferLineState state = TradeOfferLineState.ReadyToOffer;

    public TradeOfferLineOperation(
        ITradeIo io,
        TradeBatchLine line,
        TradeOfferObservation beforeOffer,
        DateTimeOffset deadline)
    {
        this.io = io;
        this.line = line;
        expectedQuantityAfterOffer = checked(QuantityIn(beforeOffer) + line.Quantity);
        this.deadline = deadline;
    }

    public bool HasDispatched => state != TradeOfferLineState.ReadyToOffer;

    private int QuantityIn(TradeOfferObservation offer) => offer.Items
        .Where(item => item.ItemId == line.ItemId && item.IsHighQuality == line.IsHighQuality)
        .Sum(item => item.Quantity);

    public TradeOfferLineResult Advance(DateTimeOffset now)
    {
        if (state is TradeOfferLineState.Completed or TradeOfferLineState.Failed)
            return Result();
        if (now > deadline)
            return Fail($"Timed out while offering {line.ItemName}.");

        if (state == TradeOfferLineState.ReadyToOffer)
        {
            if (!io.TryOfferItem(line, out var offerError))
                return string.IsNullOrWhiteSpace(offerError)
                    ? Result()
                    : Fail(offerError);

            state = line.SourceStackQuantity > 1
                ? TradeOfferLineState.WaitingForQuantityInput
                : TradeOfferLineState.WaitingForSlot;
            return Result();
        }

        if (state == TradeOfferLineState.WaitingForQuantityInput)
        {
            // FFXIV can populate the trade slot before the stack dialog is submitted.
            // That observation is deliberately not completion evidence for this line.
            if (!io.IsNumericInputOpen)
                return Result();
            if (!io.TrySubmitQuantity(line.Quantity, out var quantityError))
                return string.IsNullOrWhiteSpace(quantityError)
                    ? Result()
                    : Fail(quantityError);

            state = TradeOfferLineState.AwaitingSubmitClosure;
            return Result();
        }

        if (state == TradeOfferLineState.AwaitingSubmitClosure)
        {
            if (io.IsNumericInputOpen)
                return Result();
            state = TradeOfferLineState.WaitingForSlot;
        }

        if (state == TradeOfferLineState.WaitingForSlot)
        {
            var offer = io.ObserveOffer();
            if (offer is { SyncPending: false } && QuantityIn(offer) >= expectedQuantityAfterOffer)
                state = TradeOfferLineState.Completed;
        }

        return Result();
    }

    private TradeOfferLineResult Fail(string message)
    {
        state = TradeOfferLineState.Failed;
        return new(state, message);
    }

    private TradeOfferLineResult Result() =>
        new(state, state switch
        {
            TradeOfferLineState.ReadyToOffer => $"Ready to offer {line.ItemName}.",
            TradeOfferLineState.WaitingForQuantityInput => $"Waiting for the quantity input for {line.ItemName}.",
            TradeOfferLineState.AwaitingSubmitClosure => $"Waiting for the quantity input for {line.ItemName} to close.",
            TradeOfferLineState.WaitingForSlot => $"Waiting for {line.ItemName} to occupy its trade slot.",
            TradeOfferLineState.Completed => $"Offered {line.ItemName}.",
            _ => $"Offering {line.ItemName} failed.",
        });
}
