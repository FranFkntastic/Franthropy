namespace Franthropy.Web.Tables;

public sealed record WebTableResizeColumn(
    string Id,
    string Label,
    int DefaultWidthPx,
    int MinWidthPx = 64)
{
    public int SafeDefaultWidthPx => Math.Max(DefaultWidthPx, SafeMinWidthPx);

    public int SafeMinWidthPx => Math.Max(1, MinWidthPx);
}
