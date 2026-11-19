namespace Franthropy.Dalamud.Automation.Trades;

internal enum TradeOfferGilState
{
    ReadyToOpenInput,
    WaitingForInput,
    WaitingForAcknowledgement,
    Completed,
    Failed,
}

internal sealed record TradeOfferGilResult(
    TradeOfferGilState State,
    string Message,
    bool ActionIssued = false)
{
    public bool IsCompleted => State == TradeOfferGilState.Completed;
    public bool IsFailed => State == TradeOfferGilState.Failed;
}

// Owns the currency-input protocol for one already-planned batch amount. The runner
// supplies pacing and the batch deadline; this operation never treats opening the input as payment.
internal sealed class TradeOfferGilOperation
{
    private readonly ITradeIo io;
    private readonly int amount;
    private TradeOfferGilState state = TradeOfferGilState.ReadyToOpenInput;

    public TradeOfferGilOperation(ITradeIo io, int amount)
    {
        this.io = io;
        this.amount = amount;
    }

    public bool IsCompleted => state == TradeOfferGilState.Completed;

    public TradeOfferGilResult Advance()
    {
        if (state is TradeOfferGilState.Completed or TradeOfferGilState.Failed)
            return Result();
        if (amount <= 0)
            return Fail("Trade gil amount must be positive.");

        if (state == TradeOfferGilState.ReadyToOpenInput)
        {
            if (!io.TryOpenGilInput(out var openError))
                return string.IsNullOrWhiteSpace(openError) ? Result() : Fail(openError);

            state = TradeOfferGilState.WaitingForInput;
            return new(state, "Waiting for the trade gil input.", ActionIssued: true);
        }

        if (state == TradeOfferGilState.WaitingForAcknowledgement)
        {
            if (io.IsNumericInputOpen)
                return Result();
            var offer = io.ObserveOffer();
            if (offer == null || offer.SyncPending)
                return Result();
            if (offer.Gil != amount)
                return offer.Gil == 0 ? Result() : Fail($"The live offer contains {offer.Gil:N0} gil; expected {amount:N0}.");
            state = TradeOfferGilState.Completed;
            return Result();
        }

        if (!io.IsNumericInputOpen)
            return Result();
        if (!io.TrySubmitQuantity(amount, out var submitError))
            return string.IsNullOrWhiteSpace(submitError) ? Result() : Fail(submitError);

        state = TradeOfferGilState.WaitingForAcknowledgement;
        return new(state, "Verifying offered gil...", ActionIssued: true);
    }

    private TradeOfferGilResult Fail(string message)
    {
        state = TradeOfferGilState.Failed;
        return new(state, message);
    }

    private TradeOfferGilResult Result() => new(state, state switch
    {
        TradeOfferGilState.ReadyToOpenInput => "Ready to open the trade gil input.",
        TradeOfferGilState.WaitingForInput => "Waiting for the trade gil input.",
        TradeOfferGilState.WaitingForAcknowledgement => "Verifying offered gil...",
        TradeOfferGilState.Completed => $"Offered {amount:N0} gil.",
        _ => "Offering trade gil failed.",
    });
}
