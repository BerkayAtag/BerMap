using System;
using BerMap.Application.Services.Scheduling;
using Xunit;

namespace BerMap.Tests.Unit;

public class CronScheduleTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    private static DateTime At(int year, int month, int day, int hour = 0, int minute = 0) =>
        new(year, month, day, hour, minute, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("0 */6 * * *")]
    [InlineData("*/15 8-18 * * MON-FRI")]
    [InlineData("0 2 1,15 * *")]
    [InlineData("30 4 * JAN,jul 0")]
    [InlineData("0 0 * * 7")]
    [InlineData("5/20 * * * *")]
    [InlineData("0 0 29 2 *")]
    [InlineData("@daily")]
    [InlineData("  0   3 * * *  ")]
    public void TryParse_AcceptsValidExpressions(string expression)
    {
        Assert.True(CronSchedule.TryParse(expression, out var schedule, out var error), error);
        Assert.Equal(expression.Trim(), schedule!.Expression);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0 */6 * *")]
    [InlineData("0 */6 * * * *")]
    [InlineData("60 * * * *")]
    [InlineData("0 24 * * *")]
    [InlineData("0 0 0 * *")]
    [InlineData("0 0 * 13 *")]
    [InlineData("0 0 * * 8")]
    [InlineData("*/0 * * * *")]
    [InlineData("10-5 * * * *")]
    [InlineData("0 0 31 2 *")]
    [InlineData("a b c d e")]
    [InlineData("1,,2 * * * *")]
    public void TryParse_RejectsInvalidExpressions(string expression)
    {
        Assert.False(CronSchedule.TryParse(expression, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void Next_EverySixHours()
    {
        var schedule = CronSchedule.Parse("0 */6 * * *");

        Assert.Equal(At(2026, 11, 4, 6), schedule.GetNextOccurrence(At(2026, 11, 4, 5, 59), Utc));
        // Strictly after: an exact occurrence moves to the following one.
        Assert.Equal(At(2026, 11, 4, 12), schedule.GetNextOccurrence(At(2026, 11, 4, 6), Utc));
        Assert.Equal(At(2026, 11, 5, 0), schedule.GetNextOccurrence(At(2026, 11, 4, 18, 30), Utc));
    }

    [Fact]
    public void Next_WeekdaysOnly_SkipsWeekend()
    {
        var schedule = CronSchedule.Parse("0 9 * * MON-FRI");

        // 2026-11-06 is a Friday.
        Assert.Equal(At(2026, 11, 9, 9), schedule.GetNextOccurrence(At(2026, 11, 6, 10), Utc));
    }

    [Fact]
    public void Next_DayOfMonthOrDayOfWeek_WhenBothRestricted()
    {
        // The 15th, or any Monday (Vixie cron semantics).
        var schedule = CronSchedule.Parse("0 0 15 * 1");

        // From Tuesday 2026-11-10 the next match is the 15th (Sunday), before Monday the 16th.
        Assert.Equal(At(2026, 11, 15), schedule.GetNextOccurrence(At(2026, 11, 10, 12), Utc));
        Assert.Equal(At(2026, 11, 16), schedule.GetNextOccurrence(At(2026, 11, 15, 12), Utc));
    }

    [Fact]
    public void Next_LeapDay_FindsTheNextLeapYear()
    {
        var schedule = CronSchedule.Parse("0 0 29 2 *");
        Assert.Equal(At(2028, 2, 29), schedule.GetNextOccurrence(At(2026, 11, 4), Utc));
    }

    [Fact]
    public void Next_IsEvaluatedInTheConfiguredTimeZone()
    {
        var istanbul = TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul"); // UTC+3, no DST
        var schedule = CronSchedule.Parse("0 2 * * *");

        // 02:00 in Istanbul is 23:00 UTC on the previous day.
        Assert.Equal(At(2026, 11, 4, 23), schedule.GetNextOccurrence(At(2026, 11, 4, 12), istanbul));
    }

    [Fact]
    public void Next_SkipsLocalTimesRemovedByDaylightSaving()
    {
        var berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
        var schedule = CronSchedule.Parse("30 2 * * *");

        // 2026-03-29 02:30 does not exist in Berlin (clocks jump 02:00 -> 03:00),
        // so the next run is 02:30 CEST (00:30 UTC) on the 30th.
        Assert.Equal(At(2026, 3, 30, 0, 30), schedule.GetNextOccurrence(At(2026, 3, 28, 12), berlin));
    }
}
