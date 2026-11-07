using Switchboard.Services;
using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Switchboard;

public partial class MainWindow
{
    private readonly ShutdownScheduler _shutdown = new(new WindowsShutdownTaskBackend());
    private bool _shutdownUpdating, _shutdownAvailable = true;
    private DateTimeOffset _nextShutdownRead;
    private System.Windows.Forms.ToolStripMenuItem? _shutdownTrayCancel;

    private void InitializeShutdown()
    {
        _shutdownUpdating = true;
        try
        {
            var preferences = _settings.Shutdown;
            CountdownRadio.IsChecked = preferences.Countdown;
            AtTimeRadio.IsChecked = !preferences.Countdown;
            ShutdownForce.IsChecked = preferences.Force;
            ShutdownHours.Text = preferences.Hours.ToString(CultureInfo.InvariantCulture);
            ShutdownMinutes.Text = preferences.Minutes.ToString(CultureInfo.InvariantCulture);
            ShutdownSeconds.Text = preferences.Seconds.ToString(CultureInfo.InvariantCulture);
            SetShutdownDate(DateTime.Today);
            ShutdownClockHour.Value = preferences.ClockHour;
            ShutdownClockMinute.Value = preferences.ClockMinute;
        }
        finally { _shutdownUpdating = false; }
        UpdateShutdownUI();
    }
    private void LoadShutdown()
    {
        try
        {
            _shutdown.Refresh(); _shutdownAvailable = true;
            if (_shutdown.Current is { } plan && plan.DueAt > DateTimeOffset.Now)
            {
                _shutdownUpdating = true;
                CountdownRadio.IsChecked = plan.IsCountdown; AtTimeRadio.IsChecked = !plan.IsCountdown;
                ShutdownForce.IsChecked = plan.Force;
                var local = plan.DueAt.LocalDateTime;
                SetShutdownDate(local.Date); ShutdownClockHour.Value = local.Hour; ShutdownClockMinute.Value = local.Minute;
            }
        }
        catch (Exception error) { _shutdownAvailable = false; ShutdownFailure(error); }
        finally { _shutdownUpdating = false; _nextShutdownRead = DateTimeOffset.UtcNow.AddSeconds(5); UpdateShutdownUI(); }
    }
    private void UpdateShutdown()
    {
        if (DateTimeOffset.UtcNow >= _nextShutdownRead)
        {
            try { _shutdown.Refresh(); _shutdownAvailable = true; }
            catch (Exception error) { if (_shutdownAvailable) ShutdownFailure(error); _shutdownAvailable = false; }
            _nextShutdownRead = DateTimeOffset.UtcNow.AddSeconds(5);
        }
        UpdateShutdownUI();
    }
    private ShutdownPlan PlanFromInputs(DateTimeOffset now)
    {
        if (CountdownRadio.IsChecked == true)
        {
            if (!int.TryParse(ShutdownHours.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var hours) ||
                !int.TryParse(ShutdownMinutes.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) ||
                !int.TryParse(ShutdownSeconds.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
                throw new ArgumentException("倒计时格式无效");
            return ShutdownScheduler.Countdown(hours, minutes, seconds, now) with { Force = ShutdownForce.IsChecked == true };
        }
        var date = new DateTime(ShutdownYear.InputValue, ShutdownMonth.InputValue, ShutdownDay.InputValue);
        return ShutdownScheduler.At(date, ShutdownClockHour.InputValue, ShutdownClockMinute.InputValue, now) with { Force = ShutdownForce.IsChecked == true };
    }
    private void UpdateShutdownUI()
    {
        var now = DateTimeOffset.Now;
        RefreshShutdownWheelRanges(now.LocalDateTime);
        var plan = _shutdown.Current;
        var active = plan != null && plan.DueAt > now;
        var countdownMode = CountdownRadio.IsChecked == true;
        CountdownInputs.Visibility = countdownMode ? Visibility.Visible : Visibility.Collapsed;
        AtTimeInputs.Visibility = countdownMode ? Visibility.Collapsed : Visibility.Visible;
        ShutdownInputs.IsEnabled = CountdownRadio.IsEnabled = AtTimeRadio.IsEnabled = !active;
        ShutdownForce.IsEnabled = !active;
        if (active && ShutdownForce.IsChecked != plan!.Force)
        {
            _shutdownUpdating = true; ShutdownForce.IsChecked = plan.Force; _shutdownUpdating = false;
        }
        ShutdownStart.IsEnabled = !active && _shutdownAvailable;
        ShutdownCancel.IsEnabled = active && _shutdownAvailable;
        ShutdownState.Text = !_shutdownAvailable ? "计划服务不可用" : active ? "已启用" : plan != null ? "计划时间已到" : "未启用";
        ShutdownState.Foreground = (Brush)FindResource(active ? "Accent" : "Muted");
        ShutdownRemainingLabel.Text = active ? "剩余时间" : plan != null ? "剩余时间" : "倒计时";
        if (plan == null)
        {
            try { plan = PlanFromInputs(now); }
            catch (ArgumentException) { }
        }
        ShutdownRemaining.Text = plan == null ? "--:--:--" : ShutdownScheduler.Remaining(plan, now);
        ShutdownDueDate.Text = plan?.DueAt.LocalDateTime.ToString("yyyy.MM.dd  dddd", CultureInfo.GetCultureInfo("zh-CN")) ?? "—";
        ShutdownDueTime.Text = plan?.DueAt.LocalDateTime.ToString("HH:mm:ss") ?? "—";
        if (_shutdownTrayCancel != null) _shutdownTrayCancel.Enabled = active && _shutdownAvailable;
        if (_tray != null)
        {
            var text = active ? $"ToolHub · {plan!.DueAt.LocalDateTime:HH:mm} 关机" : "ToolHub";
            if (_tray.Text != text) _tray.Text = text;
        }
    }
    private void ShutdownStartClick(object sender, RoutedEventArgs e)
    {
        ShutdownError.Text = "";
        try
        {
            _shutdown.Refresh();
            if (_shutdown.Current is { } existing && existing.DueAt > DateTimeOffset.Now) throw new InvalidOperationException("已有正在执行的关机计划");
            if (AtTimeRadio.IsChecked == true)
            {
                RefreshShutdownWheelRanges(DateTime.Now);
                foreach (var wheel in new[] { ShutdownYear, ShutdownMonth, ShutdownDay, ShutdownClockHour, ShutdownClockMinute })
                    if (!wheel.CommitInput()) throw new ArgumentException("日期或时间数字超出范围");
            }
            var now = DateTimeOffset.Now;
            var plan = PlanFromInputs(now);
            _shutdown.Start(plan, now);
            var preferences = _settings.Shutdown;
            preferences.Countdown = plan.IsCountdown;
            preferences.Force = plan.Force;
            if (plan.IsCountdown)
            {
                preferences.Hours = int.Parse(ShutdownHours.Text); preferences.Minutes = int.Parse(ShutdownMinutes.Text); preferences.Seconds = int.Parse(ShutdownSeconds.Text);
            }
            preferences.ClockHour = ShutdownClockHour.Value; preferences.ClockMinute = ShutdownClockMinute.Value;
            Save(); _shutdownAvailable = true; _nextShutdownRead = DateTimeOffset.UtcNow.AddSeconds(5);
            StatusText.Text = $"定时关机 · {plan.DueAt.LocalDateTime:MM月dd日 HH:mm:ss}";
        }
        catch (Exception error) { ShutdownFailure(error); }
        UpdateShutdownUI();
    }
    private void CancelShutdown()
    {
        ShutdownError.Text = "";
        try { _shutdown.Cancel(); _shutdownAvailable = true; StatusText.Text = "定时关机已取消"; }
        catch (Exception error) { ShutdownFailure(error); }
        UpdateShutdownUI();
    }
    private void ShutdownCancelClick(object sender, RoutedEventArgs e) => CancelShutdown();
    private void ShutdownModeChanged(object sender, RoutedEventArgs e) => ShutdownInputChanged(sender, e);
    private void SetShutdownDate(DateTime date)
    {
        ShutdownYear.Minimum = 1; ShutdownMonth.Minimum = ShutdownDay.Minimum = 1;
        ShutdownClockHour.Minimum = ShutdownClockMinute.Minimum = 0;
        ShutdownYear.Value = date.Year; ShutdownMonth.Value = date.Month;
        ShutdownDay.Maximum = DateTime.DaysInMonth(date.Year, date.Month); ShutdownDay.Value = date.Day;
    }
    private void RefreshShutdownWheelRanges(DateTime now)
    {
        if (_shutdownUpdating) return;
        _shutdownUpdating = true;
        try
        {
            var ranges = ShutdownPickerRanges.For(now, ShutdownYear.Value, ShutdownMonth.Value, ShutdownDay.Value,
                ShutdownClockHour.Value, ShutdownClockMinute.Value);
            static void Apply(Controls.NumberWheel wheel, ShutdownWheelRange range)
            {
                if (wheel.Maximum != range.Maximum) wheel.Maximum = range.Maximum;
                if (wheel.Minimum != range.Minimum) wheel.Minimum = range.Minimum;
                if (wheel.Value != range.Value) wheel.Value = range.Value;
            }
            Apply(ShutdownYear, ranges.Year); Apply(ShutdownMonth, ranges.Month); Apply(ShutdownDay, ranges.Day);
            Apply(ShutdownClockHour, ranges.Hour); Apply(ShutdownClockMinute, ranges.Minute);
        }
        finally { _shutdownUpdating = false; }
    }
    private void ShutdownWheelChanged(object sender, RoutedPropertyChangedEventArgs<int> e)
    {
        if (_initializing || _shutdownUpdating) return;
        ShutdownError.Text = ""; UpdateShutdownUI();
    }
    private void ShutdownInputChanged(object? sender, RoutedEventArgs e)
    {
        if (_initializing || _shutdownUpdating) return;
        ShutdownError.Text = ""; UpdateShutdownUI();
    }
    private void ShutdownPresetClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        var minutes = int.Parse((string)button.Tag);
        _shutdownUpdating = true;
        ShutdownHours.Text = (minutes / 60).ToString(); ShutdownMinutes.Text = (minutes % 60).ToString(); ShutdownSeconds.Text = "0";
        _shutdownUpdating = false; ShutdownError.Text = ""; UpdateShutdownUI();
    }
    private void ShutdownFailure(Exception error)
    {
        SettingsStore.Log(error);
        ShutdownError.Text = error switch
        {
            UnauthorizedAccessException => "当前账户无法访问 Windows 关机计划",
            COMException com when com.HResult == unchecked((int)0x80070005) => "当前账户无法访问 Windows 关机计划",
            COMException => "Windows 计划任务服务不可用",
            _ => error.Message
        };
    }
    private void ShutdownNavClick(object sender, RoutedEventArgs e) => Navigate(1);
}
