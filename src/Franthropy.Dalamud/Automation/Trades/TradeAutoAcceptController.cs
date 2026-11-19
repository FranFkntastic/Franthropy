using System;
using Dalamud.Plugin.Services;

namespace Franthropy.Dalamud.Automation.Trades;

public interface ITradeAutoAcceptIo
{
    bool IsTradeOpen { get; }
    bool IsPartnerReadyForTrade { get; }
    bool CanClickReady { get; }
    bool CanConfirmTrade { get; }
    bool TryClickReady(out string error);
    bool TryConfirmTrade(out string error);
}

internal enum TradeAutoAcceptAction
{
    None,
    Ready,
    Confirm,
}

public sealed class TradeAutoAcceptController
{
    private readonly ITradeAutoAcceptIo io;
    private readonly TradeTimingOptions timing;
    private readonly IPluginLog log;
    private readonly Func<DateTimeOffset> clock;
    private TradeAutoAcceptAction pendingAction;
    private DateTimeOffset actionAvailableAt;

    public TradeAutoAcceptController(
        ITradeAutoAcceptIo io,
        TradeTimingOptions timing,
        IPluginLog log)
        : this(io, timing, log, () => DateTimeOffset.UtcNow)
    {
    }

    internal TradeAutoAcceptController(
        ITradeAutoAcceptIo io,
        TradeTimingOptions timing,
        IPluginLog log,
        Func<DateTimeOffset> clock)
    {
        this.io = io;
        this.timing = timing;
        this.log = log;
        this.clock = clock;
    }

    public void Tick(bool enabled)
    {
        if (!enabled || !io.IsTradeOpen)
        {
            pendingAction = TradeAutoAcceptAction.None;
            return;
        }

        var action = SelectAction(
            true,
            true,
            io.IsPartnerReadyForTrade,
            io.CanClickReady,
            io.CanConfirmTrade);
        if (action == TradeAutoAcceptAction.None)
        {
            pendingAction = TradeAutoAcceptAction.None;
            return;
        }

        var now = clock();
        if (action != pendingAction)
        {
            pendingAction = action;
            actionAvailableAt = now + timing.ActionDelay;
            return;
        }

        if (now < actionAvailableAt)
            return;

        switch (action)
        {
            case TradeAutoAcceptAction.Ready:
                if (io.TryClickReady(out var readyError))
                    LogSuccess("Readied the incoming trade.");
                else
                    LogFailure("ready", readyError);
                break;
            case TradeAutoAcceptAction.Confirm:
                if (io.TryConfirmTrade(out var confirmError))
                    LogSuccess("Confirmed the incoming trade.");
                else
                    LogFailure("confirm", confirmError);
                break;
        }

        pendingAction = TradeAutoAcceptAction.None;
        actionAvailableAt = now + timing.ActionDelay;
    }

    internal static TradeAutoAcceptAction SelectAction(
        bool enabled,
        bool isTradeOpen,
        bool isPartnerReady,
        bool canReady,
        bool canConfirm)
    {
        if (!enabled || !isTradeOpen)
            return TradeAutoAcceptAction.None;
        if (canConfirm)
            return TradeAutoAcceptAction.Confirm;
        return isPartnerReady && canReady
            ? TradeAutoAcceptAction.Ready
            : TradeAutoAcceptAction.None;
    }

    private void LogSuccess(string message)
    {
        log.Debug("[Franthropy] {Message}", message);
    }

    private void LogFailure(string action, string error)
    {
        if (!string.IsNullOrWhiteSpace(error))
        {
            log.Warning(
                "[Franthropy] Unable to {Action} the incoming trade automatically: {Reason}",
                action,
                error);
        }
    }
}
