using Switchboard.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Xml.Linq;

namespace Switchboard;

internal static class ShutdownDiagnostics
{
    public static void Run(string output)
    {
        var checks = new List<string>();
        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); checks.Add(name); }
        void Reject(Action action, string name) { try { action(); } catch (ArgumentException) { checks.Add(name); return; } throw new InvalidOperationException(name); }
        var now = new DateTimeOffset(2026, 11, 5, 10, 0, 0, TimeSpan.FromHours(8));
        var countdown = ShutdownScheduler.Countdown(25, 5, 7, now);
        Check(countdown.DueAt - now == new TimeSpan(25, 5, 7), "Countdown duration and date rollover");
        Check(ShutdownScheduler.Remaining(countdown, now) == "25:05:07", "Remaining hours do not wrap after 24 hours");
        Check(ShutdownScheduler.Remaining(countdown, countdown.DueAt.AddMilliseconds(-100)) == "00:00:01", "Remaining seconds round up");
        Check(ShutdownScheduler.Remaining(countdown, countdown.DueAt.AddSeconds(3)) == "00:00:00", "Expired countdown clamps to zero");
        Reject(() => ShutdownScheduler.Countdown(0, 0, 0, now), "Zero duration rejected");
        Reject(() => ShutdownScheduler.Countdown(0, 60, 0, now), "Out-of-range minutes rejected");
        Reject(() => ShutdownScheduler.Countdown(-1, 0, 0, now), "Negative duration rejected");
        var zone = TimeZoneInfo.CreateCustomTimeZone("Test+08", TimeSpan.FromHours(8), "Test", "Test");
        var specified = ShutdownScheduler.At(new DateTime(2026, 11, 6), 1, 15, now, zone);
        Check(specified.DueAt == new DateTimeOffset(2026, 11, 6, 1, 15, 0, TimeSpan.FromHours(8)) && !specified.IsCountdown, "Specified date and time use local offset");
        Reject(() => ShutdownScheduler.At(new DateTime(2026, 11, 5), 9, 0, now, zone), "Past specified time rejected");
        var pickerNow = new DateTime(2026, 11, 7, 13, 45, 32);
        var today = ShutdownPickerRanges.For(pickerNow, 2026, 11, 7, 13, 50);
        Check(today.Year.Minimum == 2026 && today.Month.Minimum == 11 && today.Day.Minimum == 7 &&
            today.Hour.Minimum == 13 && today.Minute.Minimum == 46 && today.Minute.Value == 50,
            "Today's wheels exclude past dates, hours and minutes while preserving a future selection");
        var laterHour = ShutdownPickerRanges.For(pickerNow, 2026, 11, 7, 14, 0);
        Check(laterHour.Minute.Minimum == 0 && laterHour.Minute.Value == 0, "Later hours retain their full minute range");
        var tomorrow = ShutdownPickerRanges.For(pickerNow, 2026, 11, 8, 0, 0);
        Check(tomorrow.Hour.Minimum == 0 && tomorrow.Minute.Minimum == 0, "Future dates allow the full day");
        var nextYear = ShutdownPickerRanges.For(pickerNow, 2027, 1, 1, 0, 0);
        Check(nextYear.Month.Minimum == 1 && nextYear.Day.Minimum == 1 && nextYear.Hour.Minimum == 0,
            "Future years allow earlier months and days");
        var expired = ShutdownPickerRanges.For(pickerNow, 2025, 2, 1, 23, 59);
        Check(expired.Year.Value == 2026 && expired.Month.Value == 11 && expired.Day.Value == 7 &&
            expired.Hour.Value == 13 && expired.Minute.Value == 46, "Expired selections advance to the next available minute");
        var tick = ShutdownPickerRanges.For(pickerNow.AddMinutes(1), 2026, 11, 7, 13, 46);
        Check(tick.Minute.Minimum == 47 && tick.Minute.Value == 47, "The minimum and selected minute advance with the clock");
        var midnight = ShutdownPickerRanges.For(new DateTime(2026, 11, 30, 23, 59, 1), 2026, 11, 30, 23, 59);
        Check(midnight.Month.Value == 12 && midnight.Day.Value == 1 && midnight.Hour.Value == 0 && midnight.Minute.Value == 0,
            "Midnight and month rollover advance to the next minute without skipping days");
        var newYear = ShutdownPickerRanges.For(new DateTime(2026, 12, 31, 23, 59, 0), 2026, 12, 31, 23, 59);
        Check(newYear.Year.Minimum == 2027 && newYear.Year.Value == 2027 && newYear.Month.Value == 1 && newYear.Day.Value == 1,
            "Year rollover removes the previous year");
        var leap = ShutdownPickerRanges.For(pickerNow, 2028, 2, 31, 0, 0);
        Check(leap.Day.Maximum == 29 && leap.Day.Value == 29, "Leap-year month changes retain a valid day");
        var wheel = new Controls.NumberWheel { Minimum = 46, Maximum = 59, Value = 46, Wrap = false };
        var rows = ((System.Windows.Controls.Canvas)((System.Windows.Controls.Grid)wheel.Content).Children[1]).Children
            .OfType<System.Windows.Controls.TextBlock>().Select(row => row.Text).Where(text => text.Length > 0).ToArray();
        Check(rows.Length > 0 && rows.All(text => int.Parse(text) >= 46), "The wheel renders blank rows instead of past or wrapped values");
        var fake = new MemoryBackend(); var scheduler = new ShutdownScheduler(fake);
        scheduler.Start(countdown, now);
        var reopened = new ShutdownScheduler(fake); reopened.Refresh();
        Check(reopened.Current == countdown, "Plan survives scheduler reconstruction");
        fake.FailWrites = true;
        try { scheduler.Start(specified, now); } catch (IOException) { }
        Check(scheduler.Current == countdown, "Failed registration preserves current plan");
        fake.FailWrites = false;
        scheduler.Cancel(); reopened.Refresh();
        Check(reopened.Current == null, "Cancellation removes persisted plan");
        scheduler.Start(countdown with { Force = true }, now); reopened.Refresh();
        Check(reopened.Current?.Force == true, "Forced mode survives scheduler reconstruction");
        scheduler.Cancel();
        var production = new WindowsShutdownTaskBackend();
        var xml = XDocument.Parse(production.TaskXml(countdown));
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        Check(xml.Descendants(ns + "Arguments").Single().Value == "/s /t 0", "Shutdown command does not force applications closed");
        var forcedXml = XDocument.Parse(production.TaskXml(countdown with { Force = true }));
        Check(forcedXml.Descendants(ns + "Arguments").Single().Value == "/s /f /t 0", "Forced shutdown flag is explicit and uses zero timeout");
        Check(!countdown.Force, "Forced shutdown is disabled by default");
        Check(xml.Descendants(ns + "LogonType").Single().Value == "InteractiveToken", "No password or elevated account required");
        // This real task has a harmless action, scheduled a day away. It never
        // registers a shutdown command and is removed even on a failed check.
        var native = new WindowsShutdownTaskBackend("Switchboard.Test.Shutdown." + Guid.NewGuid().ToString("N"), Path.Combine(Environment.SystemDirectory, "where.exe"), "/q SwitchboardSchedulerProbe");
        try
        {
            var future = new ShutdownPlan(DateTimeOffset.Now.AddDays(1), true);
            native.Write(future);
            var actual = native.Read();
            Check(actual != null && Math.Abs((actual.DueAt - future.DueAt).TotalSeconds) < 1 && actual.IsCountdown, "Windows Task Scheduler registration and readback");
            var changed = future with { DueAt = future.DueAt.AddDays(1), IsCountdown = false, Force = true };
            native.Write(changed);
            actual = native.Read();
            Check(actual != null && Math.Abs((actual.DueAt - changed.DueAt).TotalSeconds) < 1 && !actual.IsCountdown && actual.Force, "Windows Task Scheduler plan replacement and forced mode readback");
            native.Delete();
            Check(native.Read() == null, "Windows Task Scheduler cancellation and cleanup");
        }
        finally { native.Delete(); }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, JsonSerializer.Serialize(new { At = DateTimeOffset.Now, Checks = checks, ShutdownExecuted = false }, new JsonSerializerOptions { WriteIndented = true }));
    }
    private sealed class MemoryBackend : IShutdownTaskBackend
    {
        private ShutdownPlan? _plan;
        public bool FailWrites { get; set; }
        public ShutdownPlan? Read() => _plan;
        public void Write(ShutdownPlan plan) { if (FailWrites) throw new IOException("Test registration failure"); _plan = plan; }
        public void Delete() => _plan = null;
    }
}
