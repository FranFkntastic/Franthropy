namespace Franthropy.Dalamud.UI.Seasonal;

public enum HolidaySpiritMode
{
    Seasonal = 0,
    Always = 1,
    Off = 2,
}

public static class HolidaySpirit
{
    public static bool IsActive(HolidaySpiritMode mode, DateOnly localDate)
        => mode switch
        {
            HolidaySpiritMode.Always => true,
            HolidaySpiritMode.Off => false,
            HolidaySpiritMode.Seasonal => IsSeasonalDate(localDate),
            _ => false,
        };

    public static DateOnly GetThanksgiving(int year)
    {
        var novemberFirst = new DateOnly(year, 11, 1);
        var daysUntilThursday =
            ((int)DayOfWeek.Thursday - (int)novemberFirst.DayOfWeek + 7) % 7;
        var firstThursday = novemberFirst.AddDays(daysUntilThursday);
        return firstThursday.AddDays(21);
    }

    private static bool IsSeasonalDate(DateOnly localDate)
    {
        var thanksgiving = GetThanksgiving(localDate.Year);
        return localDate > thanksgiving
               && localDate <= new DateOnly(localDate.Year, 12, 31);
    }
}
