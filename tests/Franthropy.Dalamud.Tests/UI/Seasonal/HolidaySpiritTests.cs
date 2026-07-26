using Franthropy.Dalamud.UI.Seasonal;

namespace Franthropy.Dalamud.Tests.UI.Seasonal;

public sealed class HolidaySpiritTests
{
    [Theory]
    [InlineData(2024, 11, 28)]
    [InlineData(2025, 11, 27)]
    [InlineData(2026, 11, 26)]
    [InlineData(2027, 11, 25)]
    public void GetThanksgiving_ReturnsFourthThursday(
        int year,
        int month,
        int day)
    {
        Assert.Equal(
            new DateOnly(year, month, day),
            HolidaySpirit.GetThanksgiving(year));
    }

    [Fact]
    public void Seasonal_StartsDayAfterThanksgiving()
    {
        Assert.False(HolidaySpirit.IsActive(
            HolidaySpiritMode.Seasonal,
            new DateOnly(2026, 11, 26)));
        Assert.True(HolidaySpirit.IsActive(
            HolidaySpiritMode.Seasonal,
            new DateOnly(2026, 11, 27)));
    }

    [Fact]
    public void Seasonal_EndsAfterDecember()
    {
        Assert.True(HolidaySpirit.IsActive(
            HolidaySpiritMode.Seasonal,
            new DateOnly(2026, 12, 31)));
        Assert.False(HolidaySpirit.IsActive(
            HolidaySpiritMode.Seasonal,
            new DateOnly(2027, 1, 1)));
    }

    [Fact]
    public void Always_OverridesCalendar()
    {
        Assert.True(HolidaySpirit.IsActive(
            HolidaySpiritMode.Always,
            new DateOnly(2026, 7, 26)));
    }

    [Fact]
    public void Off_OverridesCalendar()
    {
        Assert.False(HolidaySpirit.IsActive(
            HolidaySpiritMode.Off,
            new DateOnly(2026, 12, 24)));
    }

    [Fact]
    public void UnknownMode_IsSafelyInactive()
    {
        Assert.False(HolidaySpirit.IsActive(
            (HolidaySpiritMode)999,
            new DateOnly(2026, 12, 24)));
    }
}
