using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Switchboard.Transfer;

public sealed record TransferSource(string Path, string Name, string Mime = "application/octet-stream");
public static class TransferPayloads
{
    public const string TextMime = "application/vnd.toolhub.text+utf8";
    public const string FolderMime = "application/vnd.toolhub.folder+zip";
    public const int MaxTextBytes = 1024 * 1024;
    public const int MaxFolderEntries = 10000;
    public static string Label(string name, string mime) => mime == TextMime ? "文本" : mime == FolderMime ? Path.GetFileNameWithoutExtension(name) + " · 文件夹" : name;
    public static string OutgoingDirectory => Path.Combine(SettingsStore.DirectoryPath, "outgoing");
    public static string TemporaryPath(string extension)
    { Directory.CreateDirectory(OutgoingDirectory); return Path.Combine(OutgoingDirectory, Guid.NewGuid().ToString("N") + extension); }
    public static string WriteText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new IOException("文本为空");
        var bytes = new UTF8Encoding(false, true).GetBytes(text);
        if (bytes.Length > MaxTextBytes) throw new IOException("文本上限 1 MB");
        var path = TemporaryPath(".txt"); File.WriteAllBytes(path, bytes); return path;
    }
    public static string ReadText(string path)
    {
        if (new FileInfo(path).Length > MaxTextBytes) throw new IOException("文本上限 1 MB");
        return new UTF8Encoding(false, true).GetString(File.ReadAllBytes(path));
    }
    public static void DeleteTemporary(string path)
    {
        try { if (Path.GetFullPath(path).StartsWith(Path.GetFullPath(OutgoingDirectory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) File.Delete(path); } catch { }
    }
    public static Task<string> ArchiveFolderAsync(string directory, CancellationToken cancellation) => Task.Run(() =>
    {
        var root = Path.GetFullPath(directory);
        if (!Directory.Exists(root)) throw new IOException("文件夹不存在");
        var path = TemporaryPath(".zip"); var count = 0; long bytes = 0;
        try
        {
            using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
            void AddDirectory(string folder, string relative)
            {
                cancellation.ThrowIfCancellationRequested();
                if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) throw new IOException("不支持包含链接的文件夹");
                if (relative.Length > 0) { if (++count > MaxFolderEntries) throw new IOException("文件夹条目超过 10000"); archive.CreateEntry(relative + "/"); }
                foreach (var child in Directory.EnumerateFileSystemEntries(folder))
                {
                    cancellation.ThrowIfCancellationRequested();
                    var attributes = File.GetAttributes(child);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("不支持包含链接的文件夹");
                    if (TransferRules.Name(Path.GetFileName(child)) != Path.GetFileName(child)) throw new IOException("文件夹名称不兼容：" + Path.GetFileName(child));
                    var name = relative.Length == 0 ? Path.GetFileName(child) : relative + "/" + Path.GetFileName(child);
                    if ((attributes & FileAttributes.Directory) != 0) AddDirectory(child, name);
                    else
                    {
                        if (++count > MaxFolderEntries) throw new IOException("文件夹条目超过 10000");
                        var entry = archive.CreateEntry(name, CompressionLevel.Fastest);
                        using var source = File.OpenRead(child); using var target = entry.Open();
                        var buffer = new byte[131072]; int read;
                        while ((read = source.Read(buffer)) != 0)
                        { cancellation.ThrowIfCancellationRequested(); bytes += read; if (bytes > TransferRules.MaxFileSize) throw new IOException("文件夹超过 32 GB"); target.Write(buffer, 0, read); }
                    }
                }
            }
            AddDirectory(root, ""); return path;
        }
        catch { DeleteTemporary(path); throw; }
    }, cancellation);

    public static async Task<string> PublishAsync(string part, string directory, TransferFile file, CancellationToken cancellation)
    {
        if (file.Mime == TextMime) _ = ReadText(part);
        if (file.Mime != FolderMime) return TransferRules.SaveUnique(part, directory, file.Name);
        return await Task.Run(() => ExtractFolder(part, directory, file.Name, cancellation), cancellation);
    }
    public static string ExtractFolder(string archivePath, string directory, string archiveName, CancellationToken cancellation)
    {
        var receiveRoot = Path.GetFullPath(directory); Directory.CreateDirectory(receiveRoot);
        var staging = Path.Combine(receiveRoot, ".toolhub-folder-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            using var archive = ZipFile.OpenRead(archivePath);
            if (archive.Entries.Count > MaxFolderEntries) throw new IOException("文件夹条目超过 10000");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long total = 0;
            foreach (var entry in archive.Entries)
            {
                cancellation.ThrowIfCancellationRequested();
                var name = entry.FullName.TrimEnd('/');
                if (name.Length == 0 || name.Length > 2048 || name.Contains('\\') || name.Split('/').Any(p => p is "" or "." or ".." || TransferRules.Name(p) != p) ||
                    ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0 || !names.Add(name))
                    throw new IOException("文件夹路径无效");
                if (entry.Length < 0 || entry.Length > TransferRules.MaxFileSize - total) throw new IOException("文件夹超过 32 GB");
                total += entry.Length;
            }
            var drive = new DriveInfo(Path.GetPathRoot(receiveRoot)!);
            if (drive.IsReady && drive.AvailableFreeSpace < total + 16 * 1024 * 1024) throw new IOException("接收目录空间不足");
            var prefix = Path.GetFullPath(staging) + Path.DirectorySeparatorChar;
            foreach (var entry in archive.Entries)
            {
                cancellation.ThrowIfCancellationRequested();
                var target = Path.GetFullPath(Path.Combine(staging, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
                if (!target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new IOException("文件夹路径无效");
                if (entry.FullName.EndsWith('/')) { Directory.CreateDirectory(target); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var source = entry.Open(); using var output = new FileStream(target, FileMode.CreateNew);
                var buffer = new byte[131072]; int read; long written = 0;
                while ((read = source.Read(buffer)) != 0)
                { cancellation.ThrowIfCancellationRequested(); written += read; if (written > entry.Length) throw new IOException("文件夹内容长度无效"); output.Write(buffer, 0, read); }
                if (written != entry.Length) throw new IOException("文件夹内容未完整接收");
            }
            var folderName = TransferRules.Name(Path.GetFileNameWithoutExtension(archiveName));
            for (var i = 0; i < 10000; i++)
            {
                cancellation.ThrowIfCancellationRequested();
                var target = Path.Combine(receiveRoot, i == 0 ? folderName : folderName + $" ({i})");
                try { Directory.Move(staging, target); return target; }
                catch (IOException) when (Directory.Exists(target) || File.Exists(target)) { }
            }
            throw new IOException("同名文件夹过多");
        }
        finally
        {
            var prefix = receiveRoot.EndsWith(Path.DirectorySeparatorChar) ? receiveRoot : receiveRoot + Path.DirectorySeparatorChar;
            if (Path.GetFullPath(staging).StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && Directory.Exists(staging)) Directory.Delete(staging, true);
        }
    }
}
