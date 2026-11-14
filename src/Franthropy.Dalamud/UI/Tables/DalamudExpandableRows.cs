using Dalamud.Bindings.ImGui;

namespace Franthropy.Dalamud.UI.Tables;

/// <summary>Caller-keyed expansion state and indented, full-width table details at any nesting depth.</summary>
public sealed class DalamudExpandableRows
{
    private const float DetailIndent = 24;
    private readonly HashSet<string> expanded;

    public DalamudExpandableRows(IEqualityComparer<string>? comparer = null) =>
        expanded = new(comparer ?? StringComparer.OrdinalIgnoreCase);

    public bool IsExpanded(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return expanded.Contains(key);
    }

    public void SetExpanded(string key, bool value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (value) expanded.Add(key);
        else expanded.Remove(key);
    }

    /// <summary>Toggles the key and returns its new expansion state.</summary>
    public bool Toggle(string key)
    {
        var value = !IsExpanded(key);
        SetExpanded(key, value);
        return value;
    }

    public void Clear() => expanded.Clear();

    /// <summary>Discards expansion for keys no longer in the caller's complete data set, not just visible rows.</summary>
    public void Retain(IEnumerable<string> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        expanded.IntersectWith(keys);
    }

    /// <summary>Draws only the expander. Call SameLine and draw the label in the caller's ID scope.</summary>
    /// <remarks>Keys must be unique within the current ImGui ID scope. Returns the current expansion state.</remarks>
    public bool DrawToggle(string key)
    {
        var value = IsExpanded(key);
        ImGui.PushID(key);
        try
        {
            if (ImGui.SmallButton(value ? "v###expand" : ">###expand"))
                value = Toggle(key);
        }
        finally { ImGui.PopID(); }
        return value;
    }

    /// <summary>Emits an indented detail row only when expanded. Requires an active table when expanded.</summary>
    public void DrawDetail(string key, Action draw)
    {
        ArgumentNullException.ThrowIfNull(draw);
        if (!IsExpanded(key)) return;
        DalamudTableDetailRow.Draw(key, () =>
        {
            ImGui.Indent(DetailIndent);
            try { draw(); }
            finally { ImGui.Unindent(DetailIndent); }
        });
    }
}
