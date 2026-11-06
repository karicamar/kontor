namespace Kontor.Domain.Banking;

/// <summary>
/// TARGET2 business days, the calendar SEPA settles on: Monday to Friday except New Year's Day, Good Friday,
/// Easter Monday, 1 May, 25 and 26 December. A transfer requested for a Saturday is executed on Monday.
/// </summary>
public static class BusinessCalendar
{
    public static bool IsBusinessDay(DateOnly date)
    {
        if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) return false;
        if (date is { Month: 1, Day: 1 } or { Month: 5, Day: 1 } or { Month: 12, Day: 25 } or { Month: 12, Day: 26 }) return false;
        var easter = EasterSunday(date.Year);
        return date != easter.AddDays(-2) && date != easter.AddDays(1);
    }

    public static DateOnly NextBusinessDay(DateOnly date)
    {
        while (!IsBusinessDay(date)) date = date.AddDays(1);
        return date;
    }

    /// <summary>The given date if it is a business day, otherwise the next one.</summary>
    public static DateOnly OnOrAfter(DateOnly date) => NextBusinessDay(date);

    /// <summary>Easter Sunday in the Gregorian calendar (anonymous Gregorian algorithm, Meeus/Jones/Butcher).</summary>
    public static DateOnly EasterSunday(int year)
    {
        var a = year % 19;
        var b = year / 100;
        var c = year % 100;
        var d = b / 4;
        var e = b % 4;
        var f = (b + 8) / 25;
        var g = (b - f + 1) / 3;
        var h = (19 * a + b - d - g + 15) % 30;
        var i = c / 4;
        var k = c % 4;
        var l = (32 + 2 * e + 2 * i - h - k) % 7;
        var m = (a + 11 * h + 22 * l) / 451;
        var month = (h + l - 7 * m + 114) / 31;
        var day = (h + l - 7 * m + 114) % 31 + 1;
        return new DateOnly(year, month, day);
    }
}
