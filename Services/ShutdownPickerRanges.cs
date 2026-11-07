using System;

namespace Switchboard.Services;

internal sealed record ShutdownWheelRange(int Minimum, int Maximum, int Value);
internal sealed record ShutdownPickerRanges(ShutdownWheelRange Year, ShutdownWheelRange Month,
    ShutdownWheelRange Day, ShutdownWheelRange Hour, ShutdownWheelRange Minute)
{
    public static ShutdownPickerRanges For(DateTime now, int year, int month, int day, int hour, int minute)
    {
        // Specified shutdowns have minute precision and must be strictly in the future.
        var first = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0).AddMinutes(1);
        year = Math.Clamp(year, 1, 9999); month = Math.Clamp(month, 1, 12);
        day = Math.Clamp(day, 1, DateTime.DaysInMonth(year, month));
        hour = Math.Clamp(hour, 0, 23); minute = Math.Clamp(minute, 0, 59);
        if (new DateTime(year, month, day, hour, minute, 0) < first)
        { year = first.Year; month = first.Month; day = first.Day; hour = first.Hour; minute = first.Minute; }
        static ShutdownWheelRange Range(int value, int minimum, int maximum) =>
            new(minimum, maximum, Math.Clamp(value, minimum, maximum));
        var y = Range(year, first.Year, 9999);
        var m = Range(month, y.Value == first.Year ? first.Month : 1, 12);
        var sameMonth = y.Value == first.Year && m.Value == first.Month;
        var d = Range(day, sameMonth ? first.Day : 1, DateTime.DaysInMonth(y.Value, m.Value));
        var sameDay = sameMonth && d.Value == first.Day;
        var h = Range(hour, sameDay ? first.Hour : 0, 23);
        var min = Range(minute, sameDay && h.Value == first.Hour ? first.Minute : 0, 59);
        return new(y, m, d, h, min);
    }
}
