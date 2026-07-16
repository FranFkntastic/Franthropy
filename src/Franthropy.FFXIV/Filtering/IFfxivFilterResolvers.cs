using Franthropy.Filtering.Semantics;

namespace Franthropy.FFXIV.Filtering;

public interface IFfxivFilterResolvers
{
    IFilterNamedValueResolver<FfxivItemKey> Items { get; }
    IFilterNamedValueResolver<FfxivJobKey> Jobs { get; }
    IFilterNamedValueResolver<FfxivUiCategoryKey> UiCategories { get; }
    IFilterNamedValueResolver<FfxivCharacterKey> Characters { get; }
    IFilterNamedValueResolver<FfxivRetainerKey> Retainers { get; }
    IFilterNamedValueResolver<FfxivWorldKey> Worlds { get; }
    IFilterNamedValueResolver<FfxivDataCenterKey> DataCenters { get; }
}

public sealed record FfxivFilterResolvers(
    IFilterNamedValueResolver<FfxivItemKey> Items,
    IFilterNamedValueResolver<FfxivJobKey> Jobs,
    IFilterNamedValueResolver<FfxivUiCategoryKey> UiCategories,
    IFilterNamedValueResolver<FfxivCharacterKey> Characters,
    IFilterNamedValueResolver<FfxivRetainerKey> Retainers,
    IFilterNamedValueResolver<FfxivWorldKey> Worlds,
    IFilterNamedValueResolver<FfxivDataCenterKey> DataCenters) : IFfxivFilterResolvers;
