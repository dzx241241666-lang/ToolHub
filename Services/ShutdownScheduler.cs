using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Xml.Linq;

namespace Switchboard.Services;

public sealed record ShutdownPlan(DateTimeOffset DueAt, bool IsCountdown, bool Force = false);

public interface IShutdownTaskBackend
{
    ShutdownPlan? Read();
    void Write(ShutdownPlan plan);
    void Delete();
}

public sealed class ShutdownScheduler
{
    private readonly IShutdownTaskBackend _backend;
    public ShutdownScheduler(IShutdownTaskBackend backend) => _backend = backend;
    public ShutdownPlan? Current { get; private set; }
    public void Refresh() => Current = _backend.Read();
    public void Start(ShutdownPlan plan, DateTimeOffset now)
    {
        if (plan.DueAt <= now) throw new ArgumentException("关机时间须晚于当前时间");
        _backend.Write(plan);
        Current = plan;
    }
    public void Cancel() { _backend.Delete(); Current = null; }
    public static ShutdownPlan Countdown(int hours, int minutes, int seconds, DateTimeOffset now)
    {
        if (hours is < 0 or > 999 || minutes is < 0 or > 59 || seconds is < 0 or > 59)
            throw new ArgumentException("小时范围 0–999，分钟和秒范围 0–59");
        var duration = new TimeSpan(hours, minutes, seconds);
        if (duration <= TimeSpan.Zero) throw new ArgumentException("倒计时须大于 0");
        return new(now.Add(duration), true);
    }
    public static ShutdownPlan At(DateTime date, int hour, int minute, DateTimeOffset now, TimeZoneInfo? zone = null)
    {
        if (hour is < 0 or > 23 || minute is < 0 or > 59) throw new ArgumentException("时间格式无效");
        zone ??= TimeZoneInfo.Local;
        var local = DateTime.SpecifyKind(date.Date.AddHours(hour).AddMinutes(minute), DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local) || zone.IsAmbiguousTime(local)) throw new ArgumentException("所选时间处于夏令时切换区间");
        var due = new DateTimeOffset(local, zone.GetUtcOffset(local));
        if (due <= now) throw new ArgumentException("关机时间须晚于当前时间");
        return new(due, false);
    }
    public static string Remaining(ShutdownPlan plan, DateTimeOffset now)
    {
        var total = Math.Max(0L, (long)Math.Ceiling((plan.DueAt - now).TotalSeconds));
        return $"{total / 3600:00}:{total / 60 % 60:00}:{total % 60:00}";
    }
}

// One per-user Windows task. No password or elevation.
public sealed class WindowsShutdownTaskBackend : IShutdownTaskBackend
{
    private const string Source = "Switchboard.Shutdown/v1";
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
    private readonly string _sid = WindowsIdentity.GetCurrent().User!.Value;
    private readonly string _taskName;
    private readonly string _command;
    private readonly string? _arguments;
    public WindowsShutdownTaskBackend() : this(null, Path.Combine(Environment.SystemDirectory, "shutdown.exe"), null) { }
    internal WindowsShutdownTaskBackend(string? name, string command, string? arguments)
    {
        _taskName = name ?? "Switchboard.Shutdown." + _sid;
        _command = command; _arguments = arguments;
    }
    private string ArgumentsFor(bool force) => _arguments ?? (force ? "/s /f /t 0" : "/s /t 0");
    internal string TaskXml(ShutdownPlan plan)
    {
        return new XDocument(new XElement(Ns + "Task", new XAttribute("version", "1.2"),
            new XElement(Ns + "RegistrationInfo", new XElement(Ns + "Author", _sid), new XElement(Ns + "Source", Source), new XElement(Ns + "Description", (plan.IsCountdown ? "倒计时关机" : "指定时间关机") + (plan.Force ? " · 强制关机" : ""))),
            new XElement(Ns + "Triggers", new XElement(Ns + "TimeTrigger", new XElement(Ns + "StartBoundary", plan.DueAt.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture)), new XElement(Ns + "Enabled", true))),
            new XElement(Ns + "Principals", new XElement(Ns + "Principal", new XAttribute("id", "CurrentUser"), new XElement(Ns + "UserId", _sid), new XElement(Ns + "LogonType", "InteractiveToken"), new XElement(Ns + "RunLevel", "LeastPrivilege"))),
            new XElement(Ns + "Settings", new XElement(Ns + "MultipleInstancesPolicy", "IgnoreNew"), new XElement(Ns + "DisallowStartIfOnBatteries", false), new XElement(Ns + "StopIfGoingOnBatteries", false), new XElement(Ns + "AllowHardTerminate", false), new XElement(Ns + "StartWhenAvailable", false), new XElement(Ns + "RunOnlyIfNetworkAvailable", false), new XElement(Ns + "Enabled", true), new XElement(Ns + "Hidden", false), new XElement(Ns + "RunOnlyIfIdle", false), new XElement(Ns + "WakeToRun", false), new XElement(Ns + "ExecutionTimeLimit", "PT1M"), new XElement(Ns + "Priority", 7)),
            new XElement(Ns + "Actions", new XAttribute("Context", "CurrentUser"), new XElement(Ns + "Exec", new XElement(Ns + "Command", _command), new XElement(Ns + "Arguments", ArgumentsFor(plan.Force))))))
            .ToString(SaveOptions.DisableFormatting);
    }
    private T WithFolder<T>(Func<dynamic, T> action)
    {
        object? service = null, folder = null;
        try
        {
            service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!);
            ((dynamic)service!).Connect();
            folder = ((dynamic)service!).GetFolder(@"\");
            return action((dynamic)folder);
        }
        finally { Release(folder); Release(service); }
    }
    private static void Release(object? value) { if (value != null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }
    private static bool Missing(Exception error) => error.HResult == unchecked((int)0x80070002);
    private object? GetTask(dynamic folder)
    {
        try { return folder.GetTask(_taskName); }
        catch (Exception error) when (Missing(error)) { return null; }
    }
    private ShutdownPlan ValidateTask(object task)
    {
        var xml = XDocument.Parse((string)((dynamic)task).Xml);
        var root = xml.Root!;
        var registration = root.Element(Ns + "RegistrationInfo");
        var principal = root.Element(Ns + "Principals")?.Element(Ns + "Principal");
        var exec = root.Element(Ns + "Actions")?.Element(Ns + "Exec");
        var description = (string?)registration?.Element(Ns + "Description") ?? "";
        var arguments = (string?)exec?.Element(Ns + "Arguments");
        var force = _arguments == null ? arguments == ArgumentsFor(true) : description.EndsWith(" · 强制关机", StringComparison.Ordinal);
        if ((string?)registration?.Element(Ns + "Source") != Source ||
            (string?)principal?.Element(Ns + "UserId") != _sid ||
            !string.Equals((string?)exec?.Element(Ns + "Command"), _command, StringComparison.OrdinalIgnoreCase) ||
            arguments != ArgumentsFor(force))
            throw new InvalidOperationException("同名计划由其他程序使用");
        var boundary = (string?)root.Element(Ns + "Triggers")?.Element(Ns + "TimeTrigger")?.Element(Ns + "StartBoundary");
        if (!DateTimeOffset.TryParse(boundary, CultureInfo.InvariantCulture, DateTimeStyles.None, out var due)) throw new InvalidOperationException("关机计划时间无法读取");
        return new(due, description.StartsWith("倒计时关机", StringComparison.Ordinal), force);
    }
    public ShutdownPlan? Read() => WithFolder<ShutdownPlan?>(folder =>
    {
        var task = GetTask(folder);
        if (task == null) return null;
        try { if (!(bool)((dynamic)task).Enabled) return null; return ValidateTask(task); }
        finally { Release(task); }
    });
    public void Write(ShutdownPlan plan) => WithFolder(folder =>
    {
        var existing = GetTask(folder);
        try { if (existing != null) ValidateTask(existing); }
        finally { Release(existing); }
        object registered = folder.RegisterTask(_taskName, TaskXml(plan), 6, _sid, null, 3, null);
        try { ValidateTask(registered); } finally { Release(registered); }
        return 0;
    });
    public void Delete() => WithFolder(folder =>
    {
        var task = GetTask(folder);
        if (task == null) return 0;
        try { ValidateTask(task); folder.DeleteTask(_taskName, 0); }
        catch (Exception error) when (Missing(error)) { }
        finally { Release(task); }
        return 0;
    });
}
