using ImageMagick;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Switchboard.Conversion;

internal static class ConversionDiagnostics
{
    public static async Task Run(string reportPath)
    {
        var root = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(reportPath))!, "conversion-fixtures-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var input = Path.Combine(root, "输入 files"); var output = Path.Combine(root, "输出 files"); Directory.CreateDirectory(input);
        var checks = new List<object>();
        var officeBefore = OfficeProcesses();
        async Task Check(string name, Func<Task> test)
        { try { await test(); checks.Add(new { name, passed = true, error = "" }); } catch (Exception e) { checks.Add(new { name, passed = false, error = e.ToString() }); } }
        void Assert(bool success, string text) { if (!success) throw new Exception(text); }
        Task<string> Convert(string source, string target, CancellationToken cancellation = default) => ConversionService.ConvertAsync(source, target, 90, output, null, cancellation);
        var png = Path.Combine(input, "透明 中文.png");
        using (var image = new MagickImage(MagickColors.Transparent, 96, 64)) { image.Write(png); }
        var hash = SHA256.HashData(File.ReadAllBytes(png));
        await Check("Transparent PNG → JPEG white background", async () => { using var image = new MagickImage(await Convert(png, "jpg")); Assert(image.Width == 96 && image.Height == 64, "Wrong dimensions"); var pixel = image.GetPixels().GetPixel(0, 0).ToColor()!; Assert(pixel.R > 245 && pixel.G > 245 && pixel.B > 245, "Transparency was not flattened to white"); });
        foreach (var format in new[] { "png", "webp", "bmp", "tiff", "gif", "ico", "pdf" })
            await Check("PNG → " + format, async () => { var path = await Convert(png, format); Assert(new FileInfo(path).Length > 0, "Empty output"); if (format != "pdf") { using var image = new MagickImage(path); Assert(image.Width == 96 && image.Height == 64, "Wrong dimensions"); } else Assert(Encoding.ASCII.GetString(File.ReadAllBytes(path), 0, 4) == "%PDF", "Invalid PDF header"); });
        await Check("Source preservation and collision suffix", async () => { var one = await Convert(png, "png"); var two = await Convert(png, "png"); Assert(one != two && File.Exists(one) && File.Exists(two), "Output overwritten"); Assert(hash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(png))), "Source changed"); });
        foreach (var format in new[] { MagickFormat.Avif })
            await Check(format + " decoding", async () => { var source = Path.Combine(input, "new-image." + (format == MagickFormat.Heic ? "heic" : "avif")); using (var image = new MagickImage(MagickColors.Red, 96, 64)) image.Write(source, format); using var decoded = new MagickImage(await Convert(source, "png")); Assert(decoded.Width == 96, "Incorrect decode"); });
        var heic = Environment.GetEnvironmentVariable("SWITCHBOARD_HEIC_FIXTURE");
        if (File.Exists(heic)) await Check("HEIC decoding", async () => { using var image = new MagickImage(await Convert(heic, "png")); Assert(image.Width > 0 && image.Height > 0, "Invalid HEIC output"); });
        var wav = Path.Combine(input, "音频 sample.wav"); var video = Path.Combine(input, "视频 sample.mp4");
        await Command(ConversionWorker.Tool("ffmpeg"), "-v", "error", "-nostdin", "-y", "-f", "lavfi", "-i", "sine=frequency=440:duration=1", wav);
        await Command(ConversionWorker.Tool("ffmpeg"), "-v", "error", "-nostdin", "-y", "-f", "lavfi", "-i", "color=c=red:s=160x90:r=12:d=1", "-f", "lavfi", "-i", "sine=frequency=440:duration=1", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", video);
        foreach (var format in new[] { "mp3", "wav", "flac", "m4a", "ogg", "opus" })
            await Check("WAV → " + format, async () => { var path = await Convert(wav, format); await Probe(path, "audio"); });
        foreach (var format in new[] { "mp4", "mkv", "mov", "webm", "gif", "mp3" })
            await Check("MP4 → " + format, async () => { var path = await Convert(video, format); await Probe(path, format == "mp3" ? "audio" : "video"); });
        await Check("Cancellation cleans helper tree and partial output", async () =>
        {
            var large = Path.Combine(input, "cancel.mp4");
            await Command(ConversionWorker.Tool("ffmpeg"), "-v", "error", "-nostdin", "-y", "-f", "lavfi", "-i", "testsrc2=size=1920x1080:rate=30:duration=12", "-c:v", "libx264", "-preset", "ultrafast", "-threads", "2", large);
            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(700));
            try { await Convert(large, "webm", cancel.Token); throw new Exception("Cancellation was ignored"); } catch (OperationCanceledException) { }
            Assert(!File.Exists(Path.Combine(output, "cancel.webm")), "Partial output retained");
            Assert(!Directory.Exists(Path.Combine(output, ".switchboard-convert")), "Work directory retained");
        });
        var rtf = Path.Combine(input, "document.rtf"); File.WriteAllText(rtf, "{\\rtf1\\ansi Switchboard conversion fixture.\\par Second paragraph.}", Encoding.ASCII);
        string? docx = null, pdf = null;
        await Check("RTF → DOCX", async () => { docx = await Convert(rtf, "docx"); using var zip = ZipFile.OpenRead(docx); using var reader = new StreamReader(zip.GetEntry("word/document.xml")!.Open()); Assert((await reader.ReadToEndAsync()).Contains("Switchboard conversion fixture"), "Missing document content"); });
        foreach (var format in new[] { "doc", "odt", "rtf", "txt", "pdf" })
            await Check("DOCX → " + format, async () => { var path = await Convert(docx ?? throw new Exception("DOCX conversion failed"), format); if (format == "txt") Assert(File.ReadAllText(path).Contains("Switchboard conversion fixture"), "Missing text"); if (format == "pdf") pdf = path; });
        await Check("PDF → DOCX text reflow", async () => { var path = await Convert(pdf ?? throw new Exception("PDF conversion failed"), "docx"); using var zip = ZipFile.OpenRead(path); using var reader = new StreamReader(zip.GetEntry("word/document.xml")!.Open()); Assert((await reader.ReadToEndAsync()).Contains("Switchboard conversion fixture"), "Missing PDF text"); });
        var csv = Path.Combine(input, "table.csv"); File.WriteAllText(csv, "Name,Value\r\nSwitchboard,42\r\n", new UTF8Encoding(true)); string? xlsx = null;
        await Check("CSV → XLSX", async () => { xlsx = await Convert(csv, "xlsx"); using var zip = ZipFile.OpenRead(xlsx); Assert(zip.GetEntry("xl/workbook.xml") != null, "Invalid workbook"); });
        foreach (var format in new[] { "xls", "csv", "pdf" })
            await Check("XLSX → " + format, async () => { var path = await Convert(xlsx ?? throw new Exception("XLSX conversion failed"), format); if (format == "csv") Assert(File.ReadAllText(path).Contains("Switchboard,42"), "Missing CSV content"); });
        await Check("Document cancellation cleans owned Office instance", async () =>
        {
            var source = Path.Combine(input, "cancel-document.rtf"); File.Copy(rtf, source);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var tracked = false;
            var progress = new CallbackProgress(report => { if (report.OfficePid > 0) { tracked = true; cancellation.Cancel(); } });
            try { await ConversionService.ConvertAsync(source, "docx", 90, output, progress, cancellation.Token); throw new Exception("Cancellation ignored"); } catch (OperationCanceledException) { }
            Assert(tracked, "Office process could not be tracked");
            Assert(!File.Exists(Path.Combine(output, "cancel-document.docx")), "Partial document retained");
        });
        var pptx = Path.Combine(input, "slides.pptx"); CreateOfficePresentation(pptx);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); GC.WaitForPendingFinalizers(); await Task.Delay(500);
        await Check("PPTX → PDF", async () => { var path = await Convert(pptx, "pdf"); Assert(Encoding.ASCII.GetString(File.ReadAllBytes(path), 0, 4) == "%PDF", "Invalid PDF"); });
        await Check("PPTX → PPT → PPTX", async () => { var ppt = await Convert(pptx, "ppt"); var path = await Convert(ppt, "pptx"); using var zip = ZipFile.OpenRead(path); Assert(zip.GetEntry("ppt/presentation.xml") != null, "Invalid presentation"); });
        await Check("Invalid source fails without leftovers", async () => { var bad = Path.Combine(input, "broken.png"); File.WriteAllText(bad, "invalid"); try { await Convert(bad, "jpg"); throw new Exception("Invalid source accepted"); } catch (InvalidOperationException) { } Assert(!Directory.Exists(Path.Combine(output, ".switchboard-convert")), "Work directory retained"); });
        await Check("Existing Office processes preserved; owned instances exited", async () => { await Task.Delay(1500); var after = OfficeProcesses(); Assert(officeBefore.All(p => after.Contains(p)), "An existing Office instance exited"); Assert(after.All(p => officeBefore.Contains(p)), "An owned Office process remained: " + string.Join(',', after.Except(officeBefore))); });
        File.WriteAllText(reportPath, JsonSerializer.Serialize(new { root, checks }, new JsonSerializerOptions { WriteIndented = true }));
    }
    private static HashSet<int> OfficeProcesses() => new[] { "WINWORD", "EXCEL", "POWERPNT" }.SelectMany(n => Process.GetProcessesByName(n)).Select(p => { using (p) return p.Id; }).ToHashSet();
    private sealed class CallbackProgress(Action<WorkerReport> report) : IProgress<WorkerReport> { public void Report(WorkerReport value) => report(value); }
    private static async Task Probe(string path, string type)
    { using var data = JsonDocument.Parse(await Command(ConversionWorker.Tool("ffprobe"), "-v", "error", "-show_streams", "-of", "json", path)); if (!data.RootElement.GetProperty("streams").EnumerateArray().Any(s => s.GetProperty("codec_type").GetString() == type)) throw new Exception("Missing " + type + " stream"); }
    private static async Task<string> Command(string exe, params string[] args)
    {
        var info = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true }; foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!; var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync(); await process.WaitForExitAsync(); if (process.ExitCode != 0) throw new Exception(await stderr); return await stdout;
    }
    private static void CreateOfficePresentation(string path)
    {
        var existing = Process.GetProcessesByName("POWERPNT"); var mayQuit = existing.Length == 0; foreach (var process in existing) process.Dispose();
        dynamic app = Activator.CreateInstance(Type.GetTypeFromProgID("PowerPoint.Application")!)!;
        object? document = null;
        try
        {
            document = app.Presentations.Add(0); dynamic slides = document;
            dynamic slide = slides.Slides.Add(1, 12);
            dynamic shape = slide.Shapes.AddTextbox(1, 40, 40, 500, 100);
            shape.TextFrame.TextRange.Text = "Switchboard presentation fixture";
            slides.SaveAs(path, 24, 0); slides.Close();
        }
        finally { if (document != null) Marshal.FinalReleaseComObject(document); if (mayQuit) app.Quit(); Marshal.FinalReleaseComObject(app); }
    }
}
