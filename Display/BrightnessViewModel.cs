using System;

namespace Switchboard.Display;

public sealed class BrightnessViewModel : Observable
{
    private readonly Action<BrightnessViewModel> _changed;
    private double _level;
    private string _status;
    private bool _error, _applying, _sync;
    public MonitorBrightness Monitor { get; }
    public string Id => Monitor.Id;
    public string Name => Monitor.Name;
    public string Number { get; }
    public string Details => string.Join(" · ", Array.FindAll(new[] { Monitor.Kind, Monitor.Display, Monitor.Width > 0 ? $"{Monitor.Width} × {Monitor.Height}" : "" }, s => s.Length > 0));
    public bool Primary => Monitor.Primary;
    public bool CanControl => Monitor.CanControl;
    public long Version { get; set; }
    public string Status => _applying ? "正在应用" : _status;
    public bool HasError => _error;
    public string PercentText => CanControl ? $"{_level:0}%" : "—";
    public double Level
    {
        get => _level;
        set
        {
            if (!Set(ref _level, Math.Clamp(Math.Round(value), 0, 100))) return;
            Notify(nameof(PercentText));
            if (!_sync && CanControl) _changed(this);
        }
    }
    public BrightnessViewModel(MonitorBrightness monitor, int index, Action<BrightnessViewModel> changed)
    {
        Monitor = monitor; _level = monitor.Percent; _status = monitor.Status; _changed = changed; Number = index.ToString("00");
    }
    public void Applied(int level)
    {
        _sync = true; Level = level; _sync = false;
        State("", false, false);
    }
    public void State(string message, bool error, bool applying)
    {
        _status = message; _error = error; _applying = applying;
        Notify(nameof(Status)); Notify(nameof(HasError));
    }
}
