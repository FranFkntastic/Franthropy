using System;
using System.Collections.Generic;

namespace Franthropy.Dalamud.Automation.Trades;

internal readonly record struct TradeButtonCandidate(nint Address, string Label, bool Visible, bool Enabled);

internal enum TradeButtonState { Ready, Loading, Disabled, Missing, LabelMismatch, Ambiguous }
internal sealed record TradeButtonObservation(TradeButtonState State, nint Address, string Diagnostic);

internal static class TradeButtonIdentity
{
    internal static nint FindUnique(IReadOnlyList<TradeButtonCandidate> buttons, string expectedLabel)
        => Observe(buttons, expectedLabel).Address;

    internal static TradeButtonObservation Observe(IReadOnlyList<TradeButtonCandidate> buttons, string expectedLabel)
    {
        var diagnostic = $"Expected '{expectedLabel}'; observed: " + string.Join("; ",
            System.Linq.Enumerable.Select(buttons, x => $"'{x.Label}' visible={x.Visible} enabled={x.Enabled}"));
        if (string.IsNullOrWhiteSpace(expectedLabel))
            return new(TradeButtonState.LabelMismatch, 0, diagnostic);

        TradeButtonCandidate? match = null;
        foreach (var button in buttons)
        {
            if (button.Address == 0 || !button.Visible ||
                !string.Equals(button.Label, expectedLabel, StringComparison.Ordinal))
                continue;
            if (match is not null && match.Value.Address != button.Address)
                return new(TradeButtonState.Ambiguous, 0, diagnostic);
            match = button;
        }
        return match is { Enabled: true } found ? new(TradeButtonState.Ready, found.Address, diagnostic)
            : new(match is not null ? TradeButtonState.Disabled
                : buttons.Count == 0 ? TradeButtonState.Missing : TradeButtonState.LabelMismatch, 0, diagnostic);
    }
}
