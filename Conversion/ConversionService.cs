using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Switchboard.Conversion;

public static class ConversionService
{
    public static async Task<string> ConvertAsync(string source, string target, int quality, string outputDirectory, IProgress<WorkerReport>? progress, CancellationToken cancellation)
    {
        source = Path.GetFullPath(source); outputDirectory = Path.GetFullPath(outputDirectory);
        if (!File.Exists(source) || !ConversionFormats.Supports(source, target)) throw new InvalidOperationException("不支持此转换组合");
        Directory.CreateDirectory(outputDirectory);
        var root = Path.Combine(outputDirectory, ".switchboard-convert");
        var work = Path.Combine(root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(work);
        var request = Path.Combine(work, "request.json"); var reportPath = Path.Combine(work, "report.json");
        WorkerReport? report = null;
        using var child = new Process { StartInfo = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden } };
        child.StartInfo.ArgumentList.Add("--convert-worker"); child.StartInfo.ArgumentList.Add(request);
        try
        {
            await File.WriteAllTextAsync(request, JsonSerializer.Serialize(new ConversionRequest(source, target, quality, work)), cancellation).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested(); child.Start();
            var limit = DateTime.UtcNow.AddMinutes(ConversionFormats.Category(source) == 2 ? 5 : 360);
            while (!child.HasExited)
            {
                cancellation.ThrowIfCancellationRequested();
                if (DateTime.UtcNow > limit) throw new TimeoutException("转换超时，文件可能需要密码或确认");
                ReadReport(); await Task.Delay(150, cancellation).ConfigureAwait(false);
            }
            ReadReport(); cancellation.ThrowIfCancellationRequested();
            if (child.ExitCode != 0 || report?.State != "done") throw new InvalidOperationException(report?.Error ?? "转换组件异常退出");
            var partial = Path.Combine(work, "result." + target);
            if (!File.Exists(partial) || new FileInfo(partial).Length == 0) throw new InvalidOperationException("未生成有效文件");
            var stem = Path.GetFileNameWithoutExtension(source); if (stem.Length > 140) stem = stem[..140];
            for (var i = 0; i < 10000; i++)
            {
                cancellation.ThrowIfCancellationRequested();
                var final = Path.Combine(outputDirectory, stem + (i == 0 ? "" : $" ({i})") + "." + target);
                try { File.Move(partial, final, false); return final; }
                catch (IOException) when (File.Exists(final)) { }
            }
            throw new IOException("同名文件过多");
        }
        finally
        {
            try { if (child.Id > 0 && !child.HasExited) { child.Kill(true); await child.WaitForExitAsync().ConfigureAwait(false); } } catch { }
            ReadReport();
            // COM servers can be launched by DCOM rather than as child processes.
            // Only an instance absent before automation and matching the recorded start time is owned.
            if (report?.OfficePid > 0) try
            {
                using var office = Process.GetProcessById(report.OfficePid);
                if (office.StartTime.ToUniversalTime().Ticks == report.OfficeStarted && office.ProcessName.Equals(report.OfficeName, StringComparison.OrdinalIgnoreCase)) office.Kill();
            } catch { }
            try { if (Path.GetFullPath(work).StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) Directory.Delete(work, true); } catch { }
            try { Directory.Delete(root, false); } catch { }
        }
        void ReadReport()
        { try { if (File.Exists(reportPath)) { var latest = JsonSerializer.Deserialize<WorkerReport>(File.ReadAllText(reportPath)); if (latest != null) { report = latest; progress?.Report(latest); } } } catch (IOException) { } catch (JsonException) { } }
    }
}
