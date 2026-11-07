using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Switchboard.Conversion;

public sealed record OutputFormat(string Key, string Name);
public static class ConversionFormats
{
    public static readonly string[] Images = { ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".tif", ".tiff", ".gif", ".ico", ".avif", ".heic", ".heif" };
    public static readonly string[] Audio = { ".mp3", ".wav", ".flac", ".m4a", ".aac", ".ogg", ".opus", ".wma", ".aiff", ".ape" };
    public static readonly string[] Video = { ".mp4", ".mkv", ".mov", ".avi", ".webm", ".wmv", ".flv", ".m4v", ".mpeg", ".mpg", ".ts", ".mts", ".3gp" };
    public static readonly string[] Word = { ".doc", ".docx", ".odt", ".rtf", ".txt", ".pdf" };
    public static readonly string[] Sheets = { ".xls", ".xlsx", ".ods", ".csv" };
    public static readonly string[] Slides = { ".ppt", ".pptx", ".odp" };
    public static int Category(string path)
    { var e = Path.GetExtension(path).ToLowerInvariant(); return Images.Contains(e) ? 0 : Audio.Contains(e) || Video.Contains(e) ? 1 : Word.Contains(e) || Sheets.Contains(e) || Slides.Contains(e) ? 2 : -1; }
    public static List<OutputFormat> Targets(IEnumerable<string> files, int category)
    {
        var paths = files.ToArray();
        string[] keys = category switch { 0 => new[] { "png", "jpg", "webp", "bmp", "tiff", "gif", "ico", "pdf" }, 1 => new[] { "mp4", "mkv", "mov", "webm", "mp3", "wav", "flac", "m4a", "ogg", "opus", "gif" }, _ => new[] { "pdf", "docx", "doc", "odt", "rtf", "txt", "xlsx", "xls", "csv", "pptx", "ppt" } };
        return keys.Where(k => paths.All(p => Supports(p, k))).Select(k => new OutputFormat(k, k == "csv" ? "CSV · UTF-8 / 首张工作表" : k.ToUpperInvariant())).ToList();
    }
    public static bool Supports(string source, string target)
    {
        var ext = Path.GetExtension(source).ToLowerInvariant();
        if (Images.Contains(ext)) return new[] { "png", "jpg", "webp", "bmp", "tiff", "gif", "ico", "pdf" }.Contains(target);
        if (Audio.Contains(ext)) return new[] { "mp3", "wav", "flac", "m4a", "ogg", "opus" }.Contains(target);
        if (Video.Contains(ext)) return new[] { "mp4", "mkv", "mov", "webm", "mp3", "wav", "flac", "m4a", "ogg", "opus", "gif" }.Contains(target);
        if (Sheets.Contains(ext)) return new[] { "pdf", "xlsx", "xls", "csv" }.Contains(target);
        if (Slides.Contains(ext)) return new[] { "pdf", "pptx", "ppt" }.Contains(target);
        return Word.Contains(ext) && new[] { "pdf", "docx", "doc", "odt", "rtf", "txt" }.Contains(target);
    }
}
public sealed class ConversionJob : Observable
{
    public string Source { get; }
    public string Name => Path.GetFileName(Source);
    public string Details => Path.GetExtension(Source).TrimStart('.').ToUpperInvariant() + " · " + new FileInfo(Source).Length.ToString("N0") + " B";
    private string _status = "待转换", _error = "", _output = "";
    private double _progress;
    public string Status { get => _status; set => Set(ref _status, value); }
    public string Error { get => _error; set => Set(ref _error, value); }
    public string Output { get => _output; set => Set(ref _output, value); }
    public double Progress { get => _progress; set => Set(ref _progress, value); }
    public ConversionJob(string source) { Source = source; }
}
public sealed record ConversionRequest(string Source, string Target, int Quality, string WorkDirectory);
public sealed record WorkerReport(string State, double Progress, string Error = "", int OfficePid = 0, long OfficeStarted = 0, string OfficeName = "");
