using System.Globalization;

namespace Franthropy.FFXIV;

/// <summary>Builds hosted item icon addresses.</summary>
public static class ItemIconUrl
{
    public const string DefaultBaseUrl = "https://www.garlandtools.org/files/icons/item/";

    public static string Build(int iconId, string baseUrl = DefaultBaseUrl)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(iconId);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        return $"{baseUrl.TrimEnd('/')}/{iconId.ToString(CultureInfo.InvariantCulture)}.png";
    }
}
