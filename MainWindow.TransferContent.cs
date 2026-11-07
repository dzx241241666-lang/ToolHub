using Switchboard.Transfer;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace Switchboard;

public partial class MainWindow
{
    private CancellationTokenSource? _transferPrepare;
    private void SetTransferSelectionEnabled(bool enabled)
    {
        TransferPeers.IsEnabled = TransferPick.IsEnabled = TransferFolderPick.IsEnabled = TransferTextPick.IsEnabled =
            TransferClipboardPick.IsEnabled = TransferClear.IsEnabled = enabled;
    }
    private void ClearTransferSelection()
    {
        foreach (var item in _transferFiles.Where(item => item.Temporary)) TransferPayloads.DeleteTemporary(item.Path);
        _transferFiles.Clear(); TransferFileCount.Text = "0 项内容";
    }
    private async Task AddTransferFilesAsync(IEnumerable<string> paths)
    {
        if (_transferPrepare != null || _transferSend != null) return;
        _transferPrepare = new(); var cancellation = _transferPrepare.Token;
        SetTransferSelectionEnabled(false); TransferOutgoingCancel.Visibility = Visibility.Visible;
        try
        {
            foreach (var path in paths)
            {
                cancellation.ThrowIfCancellationRequested();
                if (_transferFiles.Any(f => string.Equals(f.OriginalPath ?? f.Path, path, StringComparison.OrdinalIgnoreCase))) continue;
                if (_transferFiles.Count >= TransferRules.MaxFiles) throw new IOException("内容数量上限 512");
                if (Directory.Exists(path))
                {
                    TransferProgressLabel.Text = "整理文件夹 · " + new DirectoryInfo(path).Name; TransferProgressBar.IsIndeterminate = true;
                    var archive = await TransferPayloads.ArchiveFolderAsync(path, cancellation);
                    if (cancellation.IsCancellationRequested) { TransferPayloads.DeleteTemporary(archive); cancellation.ThrowIfCancellationRequested(); }
                    var name = new DirectoryInfo(path).Name;
                    _transferFiles.Add(new(archive, name, TransferPayloads.FolderMime, true, path, TransferRules.Name(name) + ".zip"));
                }
                else if (File.Exists(path)) _transferFiles.Add(new(path));
            }
            TransferMessage.Text = "";
        }
        catch (Exception error) { if (!_disposed) TransferFailure(error); }
        finally
        {
            _transferPrepare.Dispose(); _transferPrepare = null;
            if (!_disposed)
            {
                TransferFileCount.Text = $"{_transferFiles.Count} 项内容"; TransferProgressBar.IsIndeterminate = false;
                SetTransferSelectionEnabled(true); TransferOutgoingCancel.Visibility = Visibility.Collapsed;
            }
        }
    }
    private async void TransferAddFolder(object sender, RoutedEventArgs e)
    {
        if (_transferSend != null || _transferPrepare != null) return;
        using var dialog = new System.Windows.Forms.FolderBrowserDialog { Description = "发送文件夹", UseDescriptionForTitle = true };
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK) await AddTransferFilesAsync(new[] { dialog.SelectedPath });
    }
    private void TransferAddText(object sender, RoutedEventArgs e)
    {
        if (_transferSend != null || _transferPrepare != null) return;
        var prior = _transferFiles.Count == 1 && _transferFiles[0].Mime == TransferPayloads.TextMime ? TransferPayloads.ReadText(_transferFiles[0].Path) : "";
        var text = ShowTransferText(prior, false); if (text != null) SelectTransferText(text);
    }
    private void SelectTransferText(string text)
    {
        try
        {
            var file = TransferPayloads.WriteText(text); ClearTransferSelection();
            _transferFiles.Add(new(file, "文本", TransferPayloads.TextMime, true, null, "文本.txt"));
            TransferFileCount.Text = "1 项文本"; TransferMessage.Text = "";
        }
        catch (Exception error) { TransferFailure(error); }
    }
    private string? ShowTransferText(string value, bool readOnly)
    {
        var dialog = new Window { Title = "文本", Owner = this, Width = 620, Height = 420, MinWidth = 400, MinHeight = 280, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
        var panel = new Grid { Margin = new Thickness(24) };
        panel.RowDefinitions.Add(new() { Height = GridLength.Auto }); panel.RowDefinitions.Add(new()); panel.RowDefinitions.Add(new() { Height = GridLength.Auto });
        panel.Children.Add(new TextBlock { Text = "文本", FontSize = 22, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 16) });
        var editor = new TextBox { Text = value, IsReadOnly = readOnly, AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.Wrap, VerticalContentAlignment = VerticalAlignment.Top, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxLength = TransferPayloads.MaxTextBytes };
        Grid.SetRow(editor, 1); panel.Children.Add(editor);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var close = new Button { Content = readOnly ? "关闭" : "取消", IsCancel = true, Margin = new Thickness(0, 0, 10, 0) };
        var done = new Button { Content = readOnly ? "复制" : "确定" };
        done.Click += async (_, _) =>
        {
            if (readOnly) { done.Content = await CopyTransferTextAsync(editor.Text) ? "已复制" : "剪贴板不可用"; }
            else if (!string.IsNullOrWhiteSpace(editor.Text)) dialog.DialogResult = true;
        };
        actions.Children.Add(close); actions.Children.Add(done); Grid.SetRow(actions, 2); panel.Children.Add(actions); dialog.Content = panel;
        dialog.Loaded += (_, _) => editor.Focus();
        return dialog.ShowDialog() == true ? editor.Text : null;
    }
    private async void TransferReadClipboard(object sender, RoutedEventArgs e)
    {
        if (_transferSend != null || _transferPrepare != null) return;
        try
        {
            if (Clipboard.ContainsFileDropList())
            {
                var paths = Clipboard.GetFileDropList().Cast<string>().ToArray(); ClearTransferSelection(); await AddTransferFilesAsync(paths);
            }
            else if (Clipboard.ContainsImage())
            {
                var image = Clipboard.GetImage() ?? throw new IOException("剪贴板图片不可用");
                var file = TransferPayloads.TemporaryPath(".png");
                try { var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using var stream = File.Create(file); encoder.Save(stream); }
                catch { TransferPayloads.DeleteTemporary(file); throw; }
                ClearTransferSelection(); _transferFiles.Add(new(file, "剪贴板图片.png", "image/png", true)); TransferFileCount.Text = "1 张图片"; TransferMessage.Text = "";
            }
            else if (Clipboard.ContainsText()) SelectTransferText(Clipboard.GetText());
            else TransferMessage.Text = "剪贴板中没有可发送的内容";
        }
        catch (Exception error) { TransferFailure(error); }
    }
    private static async Task<bool> CopyTransferTextAsync(string text)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try { Clipboard.SetText(text); return true; }
            catch (ExternalException) { await Task.Delay(80); }
        }
        return false;
    }
}
