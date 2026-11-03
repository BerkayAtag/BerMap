using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace BerMap.Application.Services.Scheduling;

/// <summary>
/// Standard five-field cron expression: <c>minute hour day-of-month month day-of-week</c>.
/// Supports <c>*</c>, numbers, ranges (<c>1-5</c>), lists (<c>1,15</c>), steps
/// (<c>*/15</c>, <c>0-30/10</c>, <c>5/20</c>), month and weekday names
/// (<c>JAN</c>, <c>MON</c>), Sunday as 0 or 7, and the macros <c>@hourly</c>,
/// <c>@daily</c>, <c>@weekly</c>, <c>@monthly</c>, <c>@yearly</c>.
/// As in Vixie cron, when both day-of-month and day-of-week are restricted a
/// day matches if either field matches.
/// </summary>
public sealed class CronSchedule
{
    private static readonly string[] MonthNames =
        { "JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC" };

    private static readonly string[] DayNames = { "SUN", "MON", "TUE", "WED", "THU", "FRI", "SAT" };

    // Long enough for the rarest valid schedule (29 February, possibly on a given weekday).
    private const int MaxSearchDays = 366 * 28;

    private readonly bool[] _minutes;
    private readonly bool[] _hours;
    private readonly bool[] _daysOfMonth;
    private readonly bool[] _months;
    private readonly bool[] _daysOfWeek;
    private readonly bool _dayOfMonthRestricted;
    private readonly bool _dayOfWeekRestricted;

    private CronSchedule(string expression, bool[] minutes, bool[] hours, bool[] daysOfMonth, bool[] months,
        bool[] daysOfWeek, bool dayOfMonthRestricted, bool dayOfWeekRestricted)
    {
        Expression = expression;
        _minutes = minutes;
        _hours = hours;
        _daysOfMonth = daysOfMonth;
        _months = months;
        _daysOfWeek = daysOfWeek;
        _dayOfMonthRestricted = dayOfMonthRestricted;
        _dayOfWeekRestricted = dayOfWeekRestricted;
    }

    public string Expression { get; }

    public static CronSchedule Parse(string expression) =>
        TryParse(expression, out var schedule, out var error)
            ? schedule
            : throw new FormatException(error);

    public static bool TryParse(string? expression, [NotNullWhen(true)] out CronSchedule? schedule, out string error)
    {
        schedule = null;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(expression))
        {
            error = "The cron expression is empty.";
            return false;
        }

        var text = expression.Trim();
        var expanded = text.ToLowerInvariant() switch
        {
            "@hourly" => "0 * * * *",
            "@daily" or "@midnight" => "0 0 * * *",
            "@weekly" => "0 0 * * 0",
            "@monthly" => "0 0 1 * *",
            "@yearly" or "@annually" => "0 0 1 1 *",
            _ => text
        };

        var fields = expanded.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 5)
        {
            error = $"Expected 5 fields (minute hour day-of-month month day-of-week), found {fields.Length}.";
            return false;
        }

        if (!TryParseField(fields[0], 0, 59, null, "minute", out var minutes, out error) ||
            !TryParseField(fields[1], 0, 23, null, "hour", out var hours, out error) ||
            !TryParseField(fields[2], 1, 31, null, "day-of-month", out var daysOfMonth, out error) ||
            !TryParseField(fields[3], 1, 12, MonthNames, "month", out var months, out error) ||
            !TryParseField(fields[4], 0, 7, DayNames, "day-of-week", out var daysOfWeek, out error))
        {
            return false;
        }

        // 7 is an alias for Sunday.
        if (daysOfWeek[7])
            daysOfWeek[0] = true;

        if (!HasPossibleDay(daysOfMonth, months, fields[4] != "*" && fields[4] != "?"))
        {
            error = "The day-of-month never occurs in the selected month(s).";
            return false;
        }

        schedule = new CronSchedule(
            text, minutes, hours, daysOfMonth, months, daysOfWeek,
            dayOfMonthRestricted: fields[2] is not ("*" or "?"),
            dayOfWeekRestricted: fields[4] is not ("*" or "?"));
        return true;
    }

    /// <summary>
    /// First occurrence strictly after <paramref name="afterUtc"/>, evaluated in
    /// <paramref name="timeZone"/> and returned in UTC. Local times skipped by a
    /// daylight-saving jump do not occur; repeated local times occur once.
    /// Returns null if the schedule never fires again within the search window.
    /// </summary>
    public DateTime? GetNextOccurrence(DateTime afterUtc, TimeZoneInfo timeZone)
    {
        if (afterUtc.Kind == DateTimeKind.Unspecified)
            afterUtc = DateTime.SpecifyKind(afterUtc, DateTimeKind.Utc);
        afterUtc = afterUtc.ToUniversalTime();

        var localAfter = TimeZoneInfo.ConvertTimeFromUtc(afterUtc, timeZone);
        var firstMinute = new DateTime(localAfter.Year, localAfter.Month, localAfter.Day,
            localAfter.Hour, localAfter.Minute, 0, DateTimeKind.Unspecified).AddMinutes(1);

        var day = firstMinute.Date;
        for (var i = 0; i < MaxSearchDays; i++, day = day.AddDays(1))
        {
            if (!MatchesDay(day))
                continue;

            var startHour = day == firstMinute.Date ? firstMinute.Hour : 0;
            for (var hour = startHour; hour < 24; hour++)
            {
                if (!_hours[hour])
                    continue;

                var startMinute = day == firstMinute.Date && hour == firstMinute.Hour ? firstMinute.Minute : 0;
                for (var minute = startMinute; minute < 60; minute++)
                {
                    if (!_minutes[minute])
                        continue;

                    var local = day.AddHours(hour).AddMinutes(minute);
                    if (timeZone.IsInvalidTime(local))
                        continue;

                    var utc = TimeZoneInfo.ConvertTimeToUtc(local, timeZone);
                    if (utc > afterUtc)
                        return utc;
                }
            }
        }

        return null;
    }

    public override string ToString() => Expression;

    private bool MatchesDay(DateTime day)
    {
        if (!_months[day.Month])
            return false;

        var dayOfMonth = _daysOfMonth[day.Day];
        var dayOfWeek = _daysOfWeek[(int)day.DayOfWeek];

        return _dayOfMonthRestricted && _dayOfWeekRestricted
            ? dayOfMonth || dayOfWeek
            : dayOfMonth && dayOfWeek;
    }

    private static bool HasPossibleDay(bool[] daysOfMonth, bool[] months, bool dayOfWeekRestricted)
    {
        // With a weekday restriction the OR rule can always produce a match.
        if (dayOfWeekRestricted)
            return true;

        int[] daysInMonth = { 0, 31, 29, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31 };
        for (var month = 1; month <= 12; month++)
        {
            if (!months[month])
                continue;
            for (var day = 1; day <= daysInMonth[month]; day++)
            {
                if (daysOfMonth[day])
                    return true;
            }
        }

        return false;
    }

    private static bool TryParseField(string field, int min, int max, string[]? names, string label,
        out bool[] values, out string error)
    {
        values = new bool[max + 1];
        error = string.Empty;

        foreach (var part in field.Split(','))
        {
            if (part.Length == 0)
            {
                error = $"Empty list item in the {label} field.";
                return false;
            }

            var rangeText = part;
            var step = 1;
            var slash = part.IndexOf('/');
            if (slash >= 0)
            {
                rangeText = part[..slash];
                if (!int.TryParse(part[(slash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out step) || step < 1)
                {
                    error = $"Invalid step '{part[(slash + 1)..]}' in the {label} field.";
                    return false;
                }
            }

            int start, end;
            if (rangeText is "*" or "?")
            {
                start = min;
                end = max;
            }
            else
            {
                var dash = rangeText.IndexOf('-');
                if (dash > 0)
                {
                    if (!TryParseValue(rangeText[..dash], min, max, names, out start) ||
                        !TryParseValue(rangeText[(dash + 1)..], min, max, names, out end))
                    {
                        error = $"Invalid range '{rangeText}' in the {label} field (allowed {min}-{max}).";
                        return false;
                    }

                    if (start > end)
                    {
                        error = $"Range '{rangeText}' in the {label} field runs backwards.";
                        return false;
                    }
                }
                else
                {
                    if (!TryParseValue(rangeText, min, max, names, out start))
                    {
                        error = $"Invalid value '{rangeText}' in the {label} field (allowed {min}-{max}).";
                        return false;
                    }

                    // "5/20" means every 20 starting at 5; a bare value is just itself.
                    end = slash >= 0 ? max : start;
                }
            }

            for (var value = start; value <= end; value += step)
                values[value] = true;
        }

        return true;
    }

    private static bool TryParseValue(string text, int min, int max, IReadOnlyList<string>? names, out int value)
    {
        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value))
            return value >= min && value <= max;

        if (names != null)
        {
            for (var i = 0; i < names.Count; i++)
            {
                if (string.Equals(names[i], text, StringComparison.OrdinalIgnoreCase))
                {
                    // Month names are 1-based, weekday names 0-based.
                    value = min == 1 ? i + 1 : i;
                    return true;
                }
            }
        }

        value = 0;
        return false;
    }
}
