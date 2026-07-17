namespace Franthropy.Web.Tables;

public sealed record WebTableGroup<TGroup, TChild>(
    TGroup Value,
    IReadOnlyList<TChild> Children);
