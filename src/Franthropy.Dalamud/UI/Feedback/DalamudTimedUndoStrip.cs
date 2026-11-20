using System.Diagnostics.CodeAnalysis;
using Dalamud.Bindings.ImGui;

namespace Franthropy.Dalamud.UI.Feedback;

/// <summary>
/// Holds the most recently removed item for a limited time and draws a <see cref="DalamudUndoStrip"/>
/// offering to restore it. Pair with immediate removals instead of a confirmation.
/// </summary>
/// <remarks>
/// Only the latest removal is offered; a new <see cref="Offer"/> replaces the previous one. The offer
/// expires when its window elapses, when the user dismisses it, and when the caller calls
/// <see cref="Clear"/> (for example when the owning context changes). External invokers, such as an
/// agent bridge, restore through <see cref="TryTake"/>.
/// </remarks>
public sealed class DalamudTimedUndoStrip<TItem>
{
    private readonly TimeSpan window;
    private readonly TimeProvider timeProvider;
    private bool offered;
    private TItem? item;
    private DateTimeOffset offeredAt;
    private int lastDrawFrame = -1;

    /// <param name="window">How long an offer lasts; 15 seconds when null.</param>
    /// <param name="timeProvider">Clock for the undo window; the system clock when null.</param>
    public DalamudTimedUndoStrip(TimeSpan? window = null, TimeProvider? timeProvider = null)
    {
        this.window = window ?? TimeSpan.FromSeconds(15);
        if (this.window <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(window), "The undo window must be positive.");
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Also expire the offer when the strip was not drawn on the previous frame (its surface was hidden).</summary>
    public bool ExpireWhenHidden { get; init; }

    /// <summary>Whether an unexpired offer exists.</summary>
    public bool HasOffer => TryPeek(out _);

    /// <summary>Time left on the current offer; zero when there is none.</summary>
    public TimeSpan Remaining =>
        TryPeek(out _) ? window - (timeProvider.GetUtcNow() - offeredAt) : TimeSpan.Zero;

    /// <summary>Offers <paramref name="removed"/> for undo, replacing any earlier offer.</summary>
    public void Offer(TItem removed)
    {
        offered = true;
        item = removed;
        offeredAt = timeProvider.GetUtcNow();
    }

    /// <summary>Reads the current offer without consuming it.</summary>
    public bool TryPeek([MaybeNullWhen(false)] out TItem removed)
    {
        if (offered && timeProvider.GetUtcNow() - offeredAt > window)
            Clear();
        removed = item;
        return offered;
    }

    /// <summary>Consumes the current offer so the caller can restore it.</summary>
    public bool TryTake([MaybeNullWhen(false)] out TItem removed)
    {
        if (!TryPeek(out removed))
            return false;
        Clear();
        return true;
    }

    public void Clear()
    {
        offered = false;
        item = default;
    }

    /// <summary>Draws the strip while an offer is live; nothing otherwise.</summary>
    /// <param name="id">Unique within the window; scopes the strip's buttons.</param>
    /// <param name="describe">Strip text for the offered item, e.g. <c>removed => $"Deleted \"{removed.Name}\""</c>.</param>
    /// <param name="restored">The item to restore when this returns true.</param>
    /// <param name="itemSubmitted">Called right after each strip button is drawn; see <see cref="DalamudUndoStrip.Draw"/>.</param>
    /// <returns>True on the frame the user chose Undo; the offer is consumed.</returns>
    public bool Draw(
        string id,
        Func<TItem, string> describe,
        [MaybeNullWhen(false)] out TItem restored,
        Action<DalamudUndoStripResult>? itemSubmitted = null)
    {
        ArgumentNullException.ThrowIfNull(describe);
        restored = default;
        var frame = ImGui.GetFrameCount();
        if (ExpireWhenHidden && frame != lastDrawFrame && lastDrawFrame != frame - 1)
            Clear();
        lastDrawFrame = frame;

        if (!TryPeek(out var current))
            return false;

        switch (DalamudUndoStrip.Draw(id, describe(current), itemSubmitted))
        {
            case DalamudUndoStripResult.Undo:
                Clear();
                restored = current;
                return true;
            case DalamudUndoStripResult.Dismiss:
                Clear();
                return false;
            default:
                return false;
        }
    }
}
