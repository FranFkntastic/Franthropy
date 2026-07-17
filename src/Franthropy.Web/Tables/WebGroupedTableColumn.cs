using Microsoft.AspNetCore.Components;

namespace Franthropy.Web.Tables;

public sealed class WebGroupedTableColumn<TGroup, TChild, TColumnId>
    where TColumnId : struct, Enum
{
    public required TColumnId Id { get; init; }
    public required string Key { get; init; }
    public required string Label { get; init; }
    public string? Tooltip { get; init; }
    public int DefaultWidthPx { get; init; } = 120;
    public int MinWidthPx { get; init; } = 64;
    public bool Sortable { get; init; } = true;
    public bool SuppressGroupActivation { get; init; }
    public string? HeaderCssClass { get; init; }
    public string? GroupCellCssClass { get; init; }
    public string? ChildCellCssClass { get; init; }
    public Func<TGroup, string?>? GroupCellTitle { get; init; }
    public Func<TChild, string?>? ChildCellTitle { get; init; }
    public required RenderFragment<TGroup> GroupCell { get; init; }
    public required RenderFragment<TChild> ChildCell { get; init; }

    internal WebTableResizeColumn ToResizeColumn() => new(Key, Label, DefaultWidthPx, MinWidthPx);
}
