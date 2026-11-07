using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Windows;

namespace Switchboard;

public partial class App : System.Windows.Application
{
    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private RegisteredWaitHandle? _wait;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Length == 2 && e.Args[0] == "--convert-worker")
        { Shutdown(Conversion.ConversionWorker.Run(e.Args[1])); return; }
        if (e.Args.Length == 2 && e.Args[0] == "--conversion-test")
        {
            try { System.Threading.Tasks.Task.Run(() => Conversion.ConversionDiagnostics.Run(e.Args[1])).GetAwaiter().GetResult(); Shutdown(0); }
            catch (Exception error) { File.WriteAllText(e.Args[1], error.ToString()); Shutdown(1); }
            return;
        }
        DispatcherUnhandledException += (_, args) => { SettingsStore.Log(args.Exception); System.Windows.MessageBox.Show(args.Exception.Message, "ToolHub", MessageBoxButton.OK, MessageBoxImage.Error); args.Handled = true; };
        if (e.Args.Length == 2 && e.Args[0] == "--rapoo-diagnose")
        {
            try { var samples = System.Threading.Tasks.Task.Run(() => Wireless.RapooBattery.ProbeAsync(true)).GetAwaiter().GetResult(); File.WriteAllText(e.Args[1], JsonSerializer.Serialize(samples, new JsonSerializerOptions { WriteIndented = true })); Shutdown(0); }
            catch (Exception error) { File.WriteAllText(e.Args[1], error.ToString()); Shutdown(1); }
            return;
        }
        if (e.Args.Contains("--wireless-test"))
        {
            var output = e.Args.SkipWhile(a => a != "--wireless-test").Skip(1).First();
            try { WirelessDiagnostics.Run(output); Shutdown(0); }
            catch (Exception error) { File.WriteAllText(output, error.ToString()); Shutdown(1); }
            return;
        }
        if (e.Args.Contains("--transfer-interop"))
        {
            var directory = e.Args.SkipWhile(a => a != "--transfer-interop").Skip(1).First();
            try { System.Threading.Tasks.Task.Run(() => TransferDiagnostics.Interop(directory)).GetAwaiter().GetResult(); Shutdown(0); }
            catch (Exception error) { File.WriteAllText(Path.Combine(directory, "pc-error.txt"), error.ToString()); Shutdown(1); }
            return;
        }
        if (e.Args.Contains("--transfer-test"))
        {
            var output = e.Args.SkipWhile(a => a != "--transfer-test").Skip(1).FirstOrDefault() ?? Path.Combine(AppContext.BaseDirectory, "transfer-tests.json");
            try { System.Threading.Tasks.Task.Run(() => TransferDiagnostics.Run(output)).GetAwaiter().GetResult(); Shutdown(0); }
            catch (Exception error) { SettingsStore.Log(error); File.WriteAllText(output, error.ToString()); Shutdown(1); }
            return;
        }
        if (e.Args.Contains("--brightness-diagnose") || e.Args.Contains("--brightness-test"))
        {
            var writeTest = e.Args.Contains("--brightness-test");
            var output = e.Args.SkipWhile(a => a != (writeTest ? "--brightness-test" : "--brightness-diagnose")).Skip(1).FirstOrDefault() ?? Path.Combine(AppContext.BaseDirectory, "brightness.json");
            try { BrightnessDiagnostics.Run(output, writeTest).GetAwaiter().GetResult(); Shutdown(0); }
            catch (Exception error) { SettingsStore.Log(error); File.WriteAllText(output, error.ToString()); Shutdown(1); }
            return;
        }
        if (e.Args.Contains("--shutdown-test"))
        {
            var output = e.Args.SkipWhile(a => a != "--shutdown-test").Skip(1).FirstOrDefault() ?? Path.Combine(AppContext.BaseDirectory, "shutdown-tests.json");
            try { ShutdownDiagnostics.Run(output); Shutdown(0); }
            catch (Exception error) { SettingsStore.Log(error); File.WriteAllText(output, error.ToString()); Shutdown(1); }
            return;
        }
        if (e.Args.Contains("--diagnose") || e.Args.Contains("--smoke-test"))
        {
            var smoke = e.Args.Contains("--smoke-test");
            var output = e.Args.SkipWhile(a => a != (smoke ? "--smoke-test" : "--diagnose")).Skip(1).FirstOrDefault() ?? Path.Combine(AppContext.BaseDirectory, "diagnostics.json");
            try { Diagnostics.Run(output, smoke); Shutdown(0); } catch (Exception error) { SettingsStore.Log(error); File.WriteAllText(output, error.ToString()); Shutdown(1); }
            return;
        }
        _mutex = new Mutex(true, @"Local\Switchboard.Audio.01", out var created);
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Switchboard.Show.01");
        if (!created) { _showEvent.Set(); Shutdown(); return; }
        var window = new MainWindow(e.Args.Contains("--background")); MainWindow = window;
        _wait = ThreadPool.RegisterWaitForSingleObject(_showEvent, (_, _) => Dispatcher.BeginInvoke(window.ShowMain), null, Timeout.Infinite, false);
        window.Show();
        SessionEnding += (_, _) => window.Exit();
    }
    protected override void OnExit(ExitEventArgs e)
    {
        _wait?.Unregister(null); _showEvent?.Dispose(); _mutex?.Dispose(); base.OnExit(e);
    }
}
