using Dalamud.Bindings.ImGui;

namespace Franthropy.Dalamud.UI.Tables;

/// <summary>One hovered key shared by every cell that represents the same entity, wherever it is drawn.</summary>
/// <remarks>A key marked during frame N stays active through frame N + 1, so cells drawn before the hovered
/// one (earlier rows, parent rows of nested tables) highlight on the next frame instead of never.
/// Cells query <see cref="IsHighlighted"/> before drawing and call <see cref="MarkHovered"/> after.</remarks>
public sealed class DalamudCrossRowHighlight
{
    private readonly IEqualityComparer<string> comparer;
    private readonly Func<int> frameSource;
    private string? key;
    private int frame = int.MinValue;

    public DalamudCrossRowHighlight(IEqualityComparer<string>? comparer = null)
        : this(comparer, ImGui.GetFrameCount)
    {
    }

    internal DalamudCrossRowHighlight(IEqualityComparer<string>? comparer, Func<int> frameSource)
    {
        this.comparer = comparer ?? StringComparer.Ordinal;
        this.frameSource = frameSource ?? throw new ArgumentNullException(nameof(frameSource));
    }

    /// <summary>The key hovered this frame or last frame, or null.</summary>
    public string? ActiveKey
    {
        get
        {
            var now = frameSource();
            return frame == now || frame == unchecked(now - 1) ? key : null;
        }
    }

    public bool IsHighlighted(string key)
    {
        return !string.IsNullOrWhiteSpace(key) && ActiveKey is { } active && comparer.Equals(active, key);
    }

    /// <summary>Records the key under the mouse for this frame.</summary>
    public void MarkHovered(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return;

        this.key = key;
        frame = frameSource();
    }

    public void Clear()
    {
        key = null;
        frame = int.MinValue;
    }
}
