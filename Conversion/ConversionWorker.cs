using ImageMagick;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace Switchboard.Conversion;

internal sealed class ConversionWorker
{
    private ConversionRequest _request = null!;
    private string _reportPath = "";
    private readonly object _reportLock = new();
    private int _officePid; private long _officeStarted; private string _officeName = "";
    private string _stage = "读取文件";
    public static int Run(string requestFile)
    {
        var worker = new ConversionWorker();
        try
        {
            worker._request = JsonSerializer.Deserialize<ConversionRequest>(File.ReadAllText(requestFile)) ?? throw new IOException("转换参数无效");
            worker._reportPath = Path.Combine(worker._request.WorkDirectory, "report.json");
            if (!File.Exists(worker._request.Source) || !ConversionFormats.Supports(worker._request.Source, worker._request.Target)) throw new IOException("文件或目标格式无效");
            worker.Report("working", -1);
            switch (ConversionFormats.Category(worker._request.Source)) { case 0: worker.Image(); break; case 1: worker.Media(); break; default: worker.Office(); break; }
            worker.Report("done", 100); return 0;
        }
        catch (Exception error) { SettingsStore.Log(error); if (worker._reportPath.Length > 0) worker.Report("failed", 0, worker._stage + " · " + error.Message); return 1; }
    }
    private string Output => Path.Combine(_request.WorkDirectory, "result." + _request.Target);
    private void Report(string state, double progress, string error = "")
    {
        lock (_reportLock)
        {
            var temp = _reportPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new WorkerReport(state, progress, error, _officePid, _officeStarted, _officeName)));
            File.Move(temp, _reportPath, true);
        }
    }
    private static MagickFormat ImageFormat(string extension) => extension.TrimStart('.').ToLowerInvariant() switch
    { "png" => MagickFormat.Png, "jpg" or "jpeg" => MagickFormat.Jpeg, "webp" => MagickFormat.WebP, "bmp" => MagickFormat.Bmp, "tif" or "tiff" => MagickFormat.Tiff, "gif" => MagickFormat.Gif, "ico" => MagickFormat.Ico, "avif" => MagickFormat.Avif, "heic" or "heif" => MagickFormat.Heic, "pdf" => MagickFormat.Pdf, _ => throw new IOException("图片格式不支持") };
    private void Image()
    {
        ResourceLimits.Memory = 512UL * 1024 * 1024; ResourceLimits.Disk = 2UL * 1024 * 1024 * 1024; ResourceLimits.Thread = 2;
        var read = new MagickReadSettings { Format = ImageFormat(Path.GetExtension(_request.Source)), FrameCount = 1 };
        using var input = File.OpenRead(_request.Source);
        using var image = new MagickImage(input, read);
        image.AutoOrient(); image.Quality = (uint)Math.Clamp(_request.Quality, 1, 100);
        if (_request.Target is "jpg" or "bmp" or "pdf") { image.BackgroundColor = MagickColors.White; image.Alpha(AlphaAction.Remove); }
        if (_request.Target == "ico") { image.Resize(new MagickGeometry(256, 256) { Greater = true }); }
        image.Write(Output, ImageFormat(_request.Target));
    }
    public static string Tool(string name)
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg", name + ".exe");
        if (File.Exists(bundled)) return bundled;
        var executable = Environment.GetEnvironmentVariable("SWITCHBOARD_FFMPEG_DIR");
        if (!string.IsNullOrEmpty(executable) && File.Exists(Path.Combine(executable, name + ".exe"))) return Path.Combine(executable, name + ".exe");
        throw new IOException("音视频转换组件缺失");
    }
    private void Media()
    {
        var source = _request.Source;
        var probe = Capture(Tool("ffprobe"), new[] { "-v", "error", "-protocol_whitelist", "file,pipe", "-show_format", "-show_streams", "-of", "json", source });
        using var info = JsonDocument.Parse(probe);
        var duration = 0d;
        if (info.RootElement.TryGetProperty("format", out var format) && format.TryGetProperty("duration", out var length)) double.TryParse(length.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out duration);
        var streams = info.RootElement.GetProperty("streams").EnumerateArray().ToList();
        bool Has(string kind) => streams.Any(s => s.TryGetProperty("codec_type", out var type) && type.GetString() == kind);
        var target = _request.Target;
        var audio = new[] { "mp3", "wav", "flac", "m4a", "ogg", "opus" }.Contains(target);
        if (!Has(audio ? "audio" : "video")) throw new IOException(audio ? "源文件没有音频轨道" : "源文件没有视频轨道");
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin", "-n", "-progress", "pipe:1", "-stats_period", "0.2", "-protocol_whitelist", "file,pipe", "-i", source };
        if (audio)
        {
            args.AddRange(new[] { "-map", "0:a:0", "-vn" });
            args.AddRange(target switch { "mp3" => new[] { "-c:a", "libmp3lame", "-q:a", "2" }, "wav" => new[] { "-c:a", "pcm_s16le" }, "flac" => new[] { "-c:a", "flac" }, "m4a" => new[] { "-c:a", "aac", "-b:a", "192k" }, "ogg" => new[] { "-c:a", "libvorbis", "-q:a", "5" }, _ => new[] { "-c:a", "libopus", "-b:a", "160k" } });
        }
        else if (target == "gif") args.AddRange(new[] { "-an", "-filter_complex", "[0:v]fps=12,scale=640:-1:flags=lanczos,split[a][b];[a]palettegen[p];[b][p]paletteuse", "-loop", "0" });
        else
        {
            args.AddRange(new[] { "-map", "0:v:0", "-map", "0:a:0?", "-sn", "-dn" });
            if (target == "webm") args.AddRange(new[] { "-c:v", "libvpx-vp9", "-crf", "32", "-b:v", "0", "-deadline", "good", "-cpu-used", "4", "-c:a", "libopus" });
            else args.AddRange(new[] { "-vf", "pad=ceil(iw/2)*2:ceil(ih/2)*2", "-c:v", "libx264", "-preset", "medium", "-crf", "22", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "192k" });
            if (target is "mp4" or "mov") args.AddRange(new[] { "-movflags", "+faststart" });
        }
        args.AddRange(new[] { "-threads", "2", Output });
        using var process = Start(Tool("ffmpeg"), args);
        var errors = new StringBuilder();
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (errors) { if (errors.Length < 6000) errors.AppendLine(e.Data); } };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data?.StartsWith("out_time_us=", StringComparison.Ordinal) == true && duration > 0 && long.TryParse(e.Data[12..], out var time)) Report("working", Math.Clamp(time / (duration * 10000), 0, 99));
        };
        process.BeginErrorReadLine(); process.BeginOutputReadLine(); process.WaitForExit();
        if (process.ExitCode != 0) throw new IOException("音视频转换失败 · " + errors.ToString().Trim());
    }
    private static Process Start(string file, IEnumerable<string> args)
    {
        var info = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        return Process.Start(info) ?? throw new IOException("转换组件无法启动");
    }
    private static string Capture(string file, IEnumerable<string> args)
    {
        using var process = Start(file, args);
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30000)) { process.Kill(true); throw new TimeoutException("文件信息读取超时"); }
        var text = output.GetAwaiter().GetResult(); var detail = error.GetAwaiter().GetResult();
        if (process.ExitCode != 0) throw new IOException("文件无法读取 · " + detail.Trim());
        return text;
    }
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    private delegate bool WindowVisitor(IntPtr window, IntPtr argument);
    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowVisitor visitor, IntPtr argument);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int maximum);
    private static uint PowerPointProcess(object application, HashSet<int> before)
    {
        dynamic app = application;
        var previousCaption = (string)app.Caption;
        var marker = "Switchboard-" + Guid.NewGuid().ToString("N");
        uint Find()
        {
            uint found = 0;
            EnumWindows((window, _) => { var title = new StringBuilder(1024); GetWindowText(window, title, title.Capacity); if (!title.ToString().Contains(marker, StringComparison.Ordinal)) return true; GetWindowThreadProcessId(window, out found); return false; }, IntPtr.Zero);
            return found;
        }
        try
        {
            app.Caption = marker;
            var pid = Find();
            if (pid != 0) return pid;
            var previousState = (int)app.WindowState;
            app.WindowState = 2;
            dynamic blank = app.Presentations.Add(-1);
            try { for (var i = 0; i < 10 && pid == 0; i++) { pid = Find(); if (pid == 0) System.Threading.Thread.Sleep(20); } }
            finally { blank.Close(); Release(blank); }
            if (before.Contains((int)pid)) app.WindowState = previousState;
            return pid;
        }
        finally { app.Caption = previousCaption; }
    }
    private void Office()
    {
        if (_request.Target == "pdf" && Path.GetExtension(_request.Source).Equals(".pdf", StringComparison.OrdinalIgnoreCase)) { File.Copy(_request.Source, Output); return; }
        if (Path.GetExtension(_request.Source).Equals(".pdf", StringComparison.OrdinalIgnoreCase)) { PdfText(); if (_request.Target is "docx" or "txt") return; }
        var ext = Path.GetExtension(_request.Source).ToLowerInvariant();
        var progId = ConversionFormats.Sheets.Contains(ext) ? "Excel.Application" : ConversionFormats.Slides.Contains(ext) ? "PowerPoint.Application" : "Word.Application";
        var type = Type.GetTypeFromProgID(progId);
        if (type == null) { LibreOffice(); return; }
        var before = Process.GetProcesses().Select(p => { try { return p.Id; } finally { p.Dispose(); } }).ToHashSet();
        object? application = null, document = null;
        var owned = false; object? oldAlerts = null, oldSecurity = null; bool? oldLinks = null, oldEvents = null;
        try
        {
            _stage = "启动 Office";
            application = Activator.CreateInstance(type) ?? throw new IOException("Office 无法启动"); dynamic app = application;
            oldAlerts = app.DisplayAlerts; app.DisplayAlerts = progId == "Excel.Application" ? (object)false : progId == "PowerPoint.Application" ? 1 : 0;
            oldSecurity = app.AutomationSecurity; app.AutomationSecurity = 3;
            _stage = "准备文档";
            try
            {
                uint pid;
                if (progId == "Word.Application")
                {
                    dynamic blank = app.Documents.Add(Visible: true);
                    try { GetWindowThreadProcessId(new IntPtr((int)blank.Windows[1].Hwnd), out pid); } finally { blank.Close(0); Release(blank); }
                }
                else if (progId == "PowerPoint.Application")
                    pid = PowerPointProcess(application, before);
                else GetWindowThreadProcessId(new IntPtr((int)app.Hwnd), out pid);
                owned = pid != 0 && !before.Contains((int)pid);
                if (owned) { using var office = Process.GetProcessById((int)pid); _officePid = (int)pid; _officeStarted = office.StartTime.ToUniversalTime().Ticks; _officeName = office.ProcessName; Report("working", -1); }
            }
            catch (Exception trackingError) { SettingsStore.Log(new InvalidOperationException("Office process tracking: " + progId, trackingError)); }
            if (progId == "Word.Application")
            {
                if (owned) app.Visible = false;
                oldLinks = (bool)app.Options.UpdateLinksAtOpen; app.Options.UpdateLinksAtOpen = false;
                _stage = "打开文档";
                document = app.Documents.Open(FileName: _request.Source, ConfirmConversions: false, ReadOnly: true, AddToRecentFiles: false, PasswordDocument: "", WritePasswordDocument: "", Visible: false, OpenAndRepair: false, NoEncodingDialog: true);
                dynamic doc = document;
                _stage = "导出文档";
                if (_request.Target == "pdf") doc.ExportAsFixedFormat(Output, 17, OpenAfterExport: false);
                else doc.SaveAs2(FileName: Output, FileFormat: _request.Target switch { "docx" => 12, "doc" => 0, "odt" => 23, "rtf" => 6, "txt" => 7, _ => throw new IOException("文档目标格式无效") }, AddToRecentFiles: false, Encoding: 65001);
                doc.Close(0); document = null; Release(doc);
            }
            else if (progId == "Excel.Application")
            {
                if (owned) app.Visible = false;
                oldLinks = (bool)app.AskToUpdateLinks; app.AskToUpdateLinks = false;
                oldEvents = (bool)app.EnableEvents; app.EnableEvents = false;
                _stage = "打开表格";
                document = app.Workbooks.Open(Filename: _request.Source, UpdateLinks: 0, ReadOnly: true, Password: "", WriteResPassword: "", IgnoreReadOnlyRecommended: true, AddToMru: false);
                dynamic book = document;
                _stage = "导出表格";
                if (_request.Target == "pdf") book.ExportAsFixedFormat(0, Output);
                else { if (_request.Target == "csv") book.Worksheets[1].Activate(); book.SaveAs(Filename: Output, FileFormat: _request.Target == "xlsx" ? 51 : _request.Target == "xls" ? 56 : 62, CreateBackup: false, Local: true); }
                book.Close(false); document = null; Release(book);
            }
            else
            {
                _stage = "打开演示文稿";
                document = app.Presentations.Open(_request.Source, -1, 0, 0);
                dynamic slides = document;
                _stage = "导出演示文稿";
                slides.SaveAs(Output, _request.Target == "pdf" ? 32 : _request.Target == "pptx" ? 24 : 1, 0);
                slides.Close(); document = null; Release(slides);
            }
        }
        finally
        {
            if (document != null) { try { dynamic doc = document; if (progId == "Word.Application") doc.Close(0); else if (progId == "Excel.Application") doc.Close(false); else doc.Close(); } catch { } Release(document); }
            if (application != null)
            {
                dynamic app = application;
                try { if (oldAlerts != null) app.DisplayAlerts = oldAlerts; if (oldSecurity != null) app.AutomationSecurity = oldSecurity; } catch { }
                try { if (oldLinks != null) { if (progId == "Word.Application") app.Options.UpdateLinksAtOpen = oldLinks.Value; else app.AskToUpdateLinks = oldLinks.Value; } if (oldEvents != null) app.EnableEvents = oldEvents.Value; } catch { }
                if (owned) try { if (progId == "Word.Application") app.Quit(0); else app.Quit(); } catch { }
                Release(application);
            }
        }
    }
    private static void Release(object value) { try { if (Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value); } catch { } }
    private void PdfText()
    {
        _stage = "提取 PDF 文字";
        using var pdf = PdfDocument.Open(_request.Source);
        var pages = pdf.GetPages().Select(page => ContentOrderTextExtractor.GetText(page)).ToList();
        if (pages.All(string.IsNullOrWhiteSpace)) throw new IOException("PDF 没有可提取的文字，扫描件需要 OCR");
        if (_request.Target == "txt") { File.WriteAllText(Output, string.Join("\n\f\n", pages), new UTF8Encoding(true)); return; }
        var path = _request.Target == "docx" ? Output : Path.Combine(_request.WorkDirectory, "pdf-text.docx");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            void Add(string name, string text) { using var writer = new StreamWriter(zip.CreateEntry(name).Open()); writer.Write(text); }
            Add("[Content_Types].xml", """<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/></Types>""");
            Add("_rels/.rels", """<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/></Relationships>""");
            XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
            var body = new XElement(w + "body");
            for (var i = 0; i < pages.Count; i++)
            {
                if (i > 0) body.Add(new XElement(w + "p", new XElement(w + "r", new XElement(w + "br", new XAttribute(w + "type", "page")))));
                foreach (var line in pages[i].Replace("\r", "").Split('\n')) body.Add(new XElement(w + "p", new XElement(w + "r", new XElement(w + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), new string(line.Where(c => !char.IsControl(c) || c == '\t').ToArray())))));
            }
            Add("word/document.xml", new XDocument(new XElement(w + "document", new XAttribute(XNamespace.Xmlns + "w", w), body)).ToString());
        }
        if (_request.Target != "docx") _request = _request with { Source = path };
    }
    private void LibreOffice()
    {
        var executable = new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) }.Select(p => Path.Combine(p, "LibreOffice", "program", "soffice.com")).FirstOrDefault(File.Exists);
        if (executable == null) throw new IOException("文档转换需要 Microsoft Office 或 LibreOffice");
        var directory = Path.Combine(_request.WorkDirectory, "office-output"); Directory.CreateDirectory(directory);
        var profile = new Uri(Path.Combine(_request.WorkDirectory, "office-profile")).AbsoluteUri;
        var args = new List<string> { "-env:UserInstallation=" + profile, "--headless", "--nologo", "--nodefault", "--nofirststartwizard", "--norestore" };
        if (Path.GetExtension(_request.Source).Equals(".pdf", StringComparison.OrdinalIgnoreCase)) args.Add("--infilter=writer_pdf_import");
        args.AddRange(new[] { "--convert-to", _request.Target == "csv" ? "csv:Text - txt - csv (StarCalc):44,34,76" : _request.Target, "--outdir", directory, _request.Source });
        Capture(executable, args);
        var result = Directory.GetFiles(directory).FirstOrDefault(p => Path.GetExtension(p).Equals("." + _request.Target, StringComparison.OrdinalIgnoreCase));
        if (result == null) throw new IOException("此文档无法转换为目标格式");
        File.Move(result, Output, false);
    }
}
