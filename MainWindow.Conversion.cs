using Switchboard.Conversion;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Forms = System.Windows.Forms;

namespace Switchboard;

public partial class MainWindow
{
    private readonly ObservableCollection<ConversionJob>[] _conversionQueues = { new(), new(), new() };
    private CancellationTokenSource? _conversionCancel;
    private int _conversionCategory;
    private bool _conversionUpdating;
    private TaskCompletionSource? _conversionFinished;
    private bool _waitingForConversionExit;
    private bool WaitForConversionExit()
    {
        if (_conversionCancel == null) return false;
        _conversionCancel.Cancel();
        if (!_waitingForConversionExit) { _waitingForConversionExit = true; FinishExitAsync(); }
        return true;
    }
    private async void FinishExitAsync()
    {
        if (_conversionFinished != null) await _conversionFinished.Task;
        Close();
    }
    private void InitializeConversion()
    {
        ConvertDirectory.Text = _settings.Conversion.Directory;
        ConvertQuality.Value = _settings.Conversion.Quality;
        SelectConversionCategory(_settings.Conversion.Category);
    }
    private void ConversionNavClick(object sender, RoutedEventArgs e) => Navigate(6);
    private void SelectConversionCategory(int category)
    {
        _conversionUpdating = true;
        _conversionCategory = category;
        ConvertImageRadio.IsChecked = category == 0; ConvertMediaRadio.IsChecked = category == 1; ConvertDocumentRadio.IsChecked = category == 2;
        ConvertJobs.ItemsSource = _conversionQueues[category];
        _conversionUpdating = false;
        UpdateConversionFormats();
    }
    private void ConversionCategoryChanged(object sender, RoutedEventArgs e)
    {
        if (_initializing || _conversionUpdating || _conversionCancel != null || sender is not RadioButton { Tag: string tag }) return;
        SelectConversionCategory(int.Parse(tag)); _settings.Conversion.Category = _conversionCategory; Save();
    }
    private void UpdateConversionFormats()
    {
        var previous = (ConvertFormat.SelectedItem as OutputFormat)?.Key;
        var targets = ConversionFormats.Targets(_conversionQueues[_conversionCategory].Select(j => j.Source), _conversionCategory);
        ConvertFormat.ItemsSource = targets;
        ConvertFormat.SelectedItem = targets.FirstOrDefault(t => t.Key == previous) ?? targets.FirstOrDefault();
        ConvertQualityPanel.Visibility = _conversionCategory == 0 ? Visibility.Visible : Visibility.Collapsed;
        ConvertQueueCount.Text = $"{_conversionQueues[_conversionCategory].Count} 个文件";
        ConvertNote.Text = _conversionCategory switch
        {
            0 => "多帧图片只转换首帧；JPG、BMP、PDF 的透明区域填充白色。",
            1 => "支持音视频互转、提取音频；GIF 输出 640 px / 12 fps。",
            _ => "PDF 转文档仅保留文字；扫描件不支持识别。CSV 只导出首张工作表。"
        };
    }
    private void ConversionAddClick(object sender, RoutedEventArgs e)
    {
        var filter = _conversionCategory == 0 ? "图片|*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.tif;*.tiff;*.gif;*.ico;*.heic;*.heif;*.avif" : _conversionCategory == 1 ? "音视频|*.mp4;*.mkv;*.mov;*.avi;*.webm;*.wmv;*.flv;*.m4v;*.mpeg;*.mpg;*.ts;*.mts;*.3gp;*.mp3;*.wav;*.flac;*.m4a;*.aac;*.ogg;*.opus;*.wma;*.aiff;*.ape" : "文档|*.doc;*.docx;*.odt;*.rtf;*.txt;*.pdf;*.xls;*.xlsx;*.ods;*.csv;*.ppt;*.pptx;*.odp";
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "转换文件", Multiselect = true, Filter = filter };
        if (dialog.ShowDialog(this) == true) AddConversionFiles(dialog.FileNames);
    }
    private void AddConversionFiles(IEnumerable<string> files)
    {
        if (_conversionCancel != null) return;
        int? first = null; var unsupported = 0;
        foreach (var source in files.Where(File.Exists))
        {
            var category = ConversionFormats.Category(source);
            if (category < 0) { unsupported++; continue; }
            first ??= category;
            var queue = _conversionQueues[category];
            if (queue.Count < 300 && !queue.Any(j => j.Source.Equals(source, StringComparison.OrdinalIgnoreCase))) queue.Add(new(Path.GetFullPath(source)));
        }
        if (first != null) SelectConversionCategory(first.Value); else UpdateConversionFormats();
        ConvertMessage.Text = unsupported > 0 ? $"{unsupported} 个文件格式不支持" : "";
    }
    private void ConversionDrop(object sender, DragEventArgs e)
    { if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) AddConversionFiles(paths); e.Handled = true; }
    private void ConversionClearClick(object sender, RoutedEventArgs e) { if (_conversionCancel == null) { _conversionQueues[_conversionCategory].Clear(); UpdateConversionFormats(); } }
    private void ConversionRemoveClick(object sender, RoutedEventArgs e) { if (_conversionCancel == null && sender is Button { DataContext: ConversionJob job }) { _conversionQueues[_conversionCategory].Remove(job); UpdateConversionFormats(); } }
    private void ConversionDirectoryClick(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog { Description = "输出目录", UseDescriptionForTitle = true, SelectedPath = _settings.Conversion.Directory };
        if (dialog.ShowDialog() != Forms.DialogResult.OK) return;
        _settings.Conversion.Directory = dialog.SelectedPath; ConvertDirectory.Text = dialog.SelectedPath; Save();
    }
    private void ConversionQualityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ConvertQualityText != null) ConvertQualityText.Text = ((int)e.NewValue).ToString();
        if (_initializing) return;
        _settings.Conversion.Quality = (int)e.NewValue; Save();
    }
    private async void ConversionStartClick(object sender, RoutedEventArgs e)
    {
        if (_conversionCancel != null || ConvertFormat.SelectedItem is not OutputFormat format) return;
        var jobs = _conversionQueues[_conversionCategory].ToArray();
        if (jobs.Length == 0) { ConvertMessage.Text = "没有待转换文件"; return; }
        var directory = _settings.Conversion.Directory; var quality = _settings.Conversion.Quality;
        _conversionCancel = new(); _conversionFinished = new(TaskCreationOptions.RunContinuationsAsynchronously); SetConversionBusy(true); ConvertMessage.Text = "";
        var completed = 0; var failed = 0; var processed = 0;
        try
        {
            foreach (var job in jobs)
            {
                if (_conversionCancel.IsCancellationRequested) break;
                job.Status = "转换中"; job.Error = ""; job.Progress = 0;
                try
                {
                    job.Output = await ConversionService.ConvertAsync(job.Source, format.Key, quality, directory,
                        new Progress<WorkerReport>(report => { if (!_disposed && job.Status == "转换中") { job.Progress = Math.Max(0, report.Progress); ConvertProgress.IsIndeterminate = report.Progress < 0; ConvertProgress.Value = (processed + Math.Max(0, report.Progress) / 100) * 100d / jobs.Length; } }), _conversionCancel.Token);
                    job.Status = "已完成"; job.Progress = 100; completed++;
                }
                catch (OperationCanceledException) { job.Status = "已取消"; break; }
                catch (Exception error) { job.Status = "失败"; job.Error = error.Message; failed++; SettingsStore.Log(error); }
                processed++;
            }
            ConvertMessage.Text = _conversionCancel.IsCancellationRequested ? $"已取消 · {completed} 个完成" : $"{completed} 个完成" + (failed > 0 ? $" · {failed} 个失败" : "");
        }
        finally { _conversionCancel.Dispose(); _conversionCancel = null; if (!_disposed) { SetConversionBusy(false); ConvertProgress.IsIndeterminate = false; ConvertProgress.Value = processed * 100d / jobs.Length; } _conversionFinished.TrySetResult(); }
    }
    private void SetConversionBusy(bool busy)
    {
        ConvertControls.IsEnabled = ConvertPick.IsEnabled = ConvertClear.IsEnabled = ConvertJobs.IsEnabled = ConvertCategories.IsEnabled = !busy;
        ConvertStart.IsEnabled = !busy; ConvertCancel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }
    private void ConversionCancelClick(object sender, RoutedEventArgs e) => _conversionCancel?.Cancel();
    private void ConversionOpenDirectory(object sender, RoutedEventArgs e)
    {
        var directory = sender is Button { DataContext: ConversionJob job } && File.Exists(job.Output) ? Path.GetDirectoryName(job.Output)! : _settings.Conversion.Directory;
        if (Directory.Exists(directory)) Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
    }
}
