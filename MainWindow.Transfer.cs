using Switchboard.Transfer;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace Switchboard;

public sealed record PickedFile(string Path, string? Label = null, string Mime = "application/octet-stream", bool Temporary = false, string? OriginalPath = null, string? PayloadName = null)
{
    public string Name => Label ?? System.IO.Path.GetFileName(Path);
    public string Details => Name + " · " + (Mime == TransferPayloads.FolderMime ? "文件夹 · " : Mime == TransferPayloads.TextMime ? "文本 · " : "") + TransferRules.Size(new FileInfo(Path).Length);
    public TransferSource Source => new(Path, PayloadName ?? Name, Mime);
}
public sealed record TransferLog(string Title, string Details, string[] Paths, bool Received, string? Text = null)
{
    public bool CanCopy => Received || Text != null;
    public string CopyLabel => Text == null ? "复制文件" : "复制文本";
    public string OpenLabel => Text == null ? "文件夹" : "查看文本";
}

public partial class MainWindow
{
    private TransferService? _transfer;
    private readonly ObservableCollection<TransferPeer> _transferPeers = new();
    private readonly ObservableCollection<PickedFile> _transferFiles = new();
    private readonly ObservableCollection<TransferLog> _transferLogs = new();
    private readonly DispatcherTimer _transferTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private CancellationTokenSource? _transferSend;
    private ReceiveOffer? _transferOffer;
    private bool _transferChanging;
    private Task? _transferStartup;
    private string _peerSignature = "";
    private void InitializeTransfer()
    {
        TransferPeers.ItemsSource = _transferPeers; TransferFiles.ItemsSource = _transferFiles; TransferHistory.ItemsSource = _transferLogs;
        TransferName.Text = _settings.Transfer.Name; TransferAutoCopy.IsChecked = _settings.Transfer.CopyToClipboard;
        TransferDirectory.Text = _settings.Transfer.ReceiveDirectory;
        _transferTimer.Tick += (_, _) =>
        {
            if (_disposed) return;
            if (_transferOffer != null && !_transferOffer.Pending) { _transferOffer = null; TransferIncoming.Visibility = Visibility.Collapsed; }
            if (_transfer == null) return;
            var peers = _transfer.Peers;
            var signature = string.Join('|', peers.Select(p => p.Info.Id + p.Name + p.Address + p.Info.Port + p.Info.CertificateSha256));
            if (_peerSignature == signature) return;
            _peerSignature = signature;
            var selection = (TransferPeers.SelectedItem as TransferPeer)?.Info.Id;
            _transferPeers.Clear(); foreach (var peer in peers) _transferPeers.Add(peer);
            TransferPeers.SelectedItem = _transferPeers.FirstOrDefault(p => p.Info.Id == selection) ?? _transferPeers.FirstOrDefault();
            TransferPeerCount.Text = peers.Count.ToString("00");
        };
        _transferTimer.Start();
    }
    private void TransferUi(Action action)
    {
        if (_disposed || Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(new Action(() => { if (!_disposed) action(); }));
    }
    private Task StartTransferAsync()
    {
        if (_transfer != null || _disposed) return Task.CompletedTask;
        return _transferStartup ??= StartTransferCoreAsync();
    }
    private async Task StartTransferCoreAsync()
    {
        await Task.Yield();
        if (_transfer != null || _transferChanging || _disposed) return;
        _transferChanging = true; TransferRetry.Visibility = Visibility.Collapsed; TransferState.Text = "启动中";
        try
        {
            var service = await Task.Run(() => new TransferService(new(_settings.Transfer.Name, SettingsStore.DirectoryPath, _settings.Transfer.ReceiveDirectory)));
            service.Offered += offer => TransferUi(() =>
            {
                _transferOffer = offer; TransferIncomingName.Text = offer.PeerName + " · " + offer.Address;
                TransferIncomingDetails.Text = offer.Files.Count == 1 && offer.Files[0].Mime == TransferPayloads.TextMime ? "文本 · " + TransferRules.Size(offer.Bytes) : $"{offer.Files.Count} 项内容 · {TransferRules.Size(offer.Bytes)}";
                TransferIncoming.Visibility = Visibility.Visible;
                StatusText.Text = "接收请求 · " + offer.PeerName;
                if (_tray != null) _tray.ShowBalloonTip(5000, "接收请求", offer.PeerName + $" · {offer.Files.Count} 项内容", Forms.ToolTipIcon.Info);
            });
            service.Received += batch => TransferUi(() => _ = ReceivedTransferAsync(batch));
            service.Progress += progress => TransferUi(() =>
            {
                TransferProgressLabel.Text = progress.Direction + " · " + progress.PeerName + " · " + progress.FileName;
                TransferProgressBar.IsIndeterminate = progress.FileName.StartsWith("校验中") || progress.FileName.StartsWith("整理文件夹") || progress.FileName == "等待接收";
                TransferProgressBar.Value = progress.Total == 0 ? 100 : Math.Clamp(progress.Completed * 100d / progress.Total, 0, 100);
                TransferProgressBytes.Text = TransferRules.Size(progress.Completed) + " / " + TransferRules.Size(progress.Total);
            });
            service.Notice += notice => TransferUi(() => TransferMessage.Text = notice);
            try { await service.StartAsync(); }
            catch { await service.DisposeAsync(); throw; }
            if (_disposed) { await service.DisposeAsync(); return; }
            _transfer = service;
            var addresses = TransferService.Addresses();
            TransferAddress.Text = addresses.Count == 0 ? "没有可用的局域网地址" : string.Join("  /  ", addresses.Select(ip => ip + ":" + service.Self.Port));
            TransferState.Text = "运行中"; TransferMessage.Text = "";
            _settings.Transfer.Enabled = true; Save();
        }
        catch (Exception error)
        {
            TransferFailure(error); TransferState.Text = "启动失败"; _settings.Transfer.Enabled = false;
            TransferRetry.Visibility = Visibility.Visible; Save();
        }
        finally { _transferChanging = false; _transferStartup = null; }
    }
    private async void TransferRetryClick(object sender, RoutedEventArgs e) => await StartTransferAsync();
    private void TransferNameChanged(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        var name = TransferName.Text.Trim(); if (name.Length == 0) name = Environment.MachineName;
        _settings.Transfer.Name = name; TransferName.Text = name; _transfer?.SetName(name); Save();
    }
    private void TransferCopyChanged(object sender, RoutedEventArgs e)
    { if (_initializing) return; _settings.Transfer.CopyToClipboard = TransferAutoCopy.IsChecked == true; Save(); }
    private void TransferChooseDirectory(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog { Description = "接收目录", SelectedPath = _settings.Transfer.ReceiveDirectory, UseDescriptionForTitle = true };
        if (dialog.ShowDialog() != Forms.DialogResult.OK) return;
        _settings.Transfer.ReceiveDirectory = dialog.SelectedPath; TransferDirectory.Text = dialog.SelectedPath; _transfer?.SetReceiveDirectory(dialog.SelectedPath); Save();
    }
    private async void TransferAddFiles(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = "传输文件" };
        if (dialog.ShowDialog(this) == true) await AddTransferFilesAsync(dialog.FileNames);
    }
    private void TransferClearFiles(object sender, RoutedEventArgs e) { if (_transferSend != null || _transferPrepare != null) return; ClearTransferSelection(); }
    private async void TransferDrop(object sender, DragEventArgs e)
    { e.Handled = true; if (_transferSend == null && _transferPrepare == null && e.Data.GetData(DataFormats.FileDrop) is string[] paths) await AddTransferFilesAsync(paths); }
    private async void TransferAddPeer(object sender, RoutedEventArgs e)
    {
        await StartTransferAsync();
        if (_transfer == null) return;
        try { var peer = await _transfer.AddPeerAsync(TransferPeerAddress.Text); _transferPeers.Remove(_transferPeers.FirstOrDefault(p => p.Info.Id == peer.Info.Id)!); _transferPeers.Add(peer); TransferPeers.SelectedItem = peer; TransferMessage.Text = ""; }
        catch (Exception error) { TransferFailure(error); }
    }
    private async void TransferDiscover(object sender, RoutedEventArgs e)
    { await StartTransferAsync(); if (_transfer != null) await _transfer.DiscoverAsync(); }
    private async void TransferDeviceClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: TransferPeer peer }) return;
        if (_transferSend != null || _transferPrepare != null) return;
        await StartTransferAsync(); if (_transfer == null || _transferSend != null) return;
        if (_transferFiles.Count == 0)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = "传输至 " + peer.Name };
            if (dialog.ShowDialog(this) != true) return;
            await AddTransferFilesAsync(dialog.FileNames);
            if (_transferFiles.Count == 0) return;
        }
        _transferSend = new(); SetTransferSelectionEnabled(false); TransferOutgoingCancel.Visibility = Visibility.Visible; TransferMessage.Text = "";
        var selection = _transferFiles.ToArray(); var paths = selection.Select(f => f.OriginalPath ?? f.Path).ToArray();
        try
        {
            await _transfer.SendAsync(peer, selection.Select(f => f.Source).ToArray(), _transferSend.Token);
            var text = selection.Length == 1 && selection[0].Mime == TransferPayloads.TextMime ? TransferPayloads.ReadText(selection[0].Path) : null;
            AddTransferLog(new("已发送 · " + peer.Name, string.Join("、", selection.Select(f => f.Name)), paths, false, text));
            ClearTransferSelection();
            TransferMessage.Text = "发送完成";
        }
        catch (Exception error) { TransferFailure(error); }
        finally { _transferSend.Dispose(); _transferSend = null; if (!_disposed) { SetTransferSelectionEnabled(true); TransferOutgoingCancel.Visibility = Visibility.Collapsed; TransferProgressBar.IsIndeterminate = false; } }
    }
    private void TransferCancelOutgoing(object sender, RoutedEventArgs e) { _transferSend?.Cancel(); _transferPrepare?.Cancel(); }
    private void TransferAccept(object sender, RoutedEventArgs e) { _transferOffer?.Accept(TransferAutoCopy.IsChecked == true); TransferIncoming.Visibility = Visibility.Collapsed; }
    private void TransferReject(object sender, RoutedEventArgs e) { _transferOffer?.Reject(); TransferIncoming.Visibility = Visibility.Collapsed; }
    private async Task ReceivedTransferAsync(ReceiveBatch batch)
    {
        var copied = false;
        if (batch.CopyToClipboard) copied = batch.Text != null ? await CopyTransferTextAsync(batch.Text) : await CopyTransferFilesAsync(batch.Paths);
        var details = batch.Text == null ? string.Join("、", batch.Paths.Select(Path.GetFileName)) : batch.Text.Replace('\r', ' ').Replace('\n', ' ');
        if (details.Length > 120) details = details[..120] + "…";
        AddTransferLog(new("已接收 · " + batch.PeerName, details + (copied ? " · 已复制" : ""), batch.Paths.ToArray(), true, batch.Text));
        TransferMessage.Text = batch.CopyToClipboard && !copied ? "接收完成，剪贴板暂不可用" : copied ? "接收完成 · 已复制到剪贴板" : "接收完成";
        StatusText.Text = batch.PeerName + $" · 已接收 {batch.Paths.Count} 项内容";
    }
    private void AddTransferLog(TransferLog log) { _transferLogs.Insert(0, log); while (_transferLogs.Count > 30) _transferLogs.RemoveAt(_transferLogs.Count - 1); }
    private static async Task<bool> CopyTransferFilesAsync(IEnumerable<string> paths)
    {
        var existing = paths.Where(p => File.Exists(p) || Directory.Exists(p)).ToArray(); if (existing.Length == 0) return false;
        var list = new StringCollection(); list.AddRange(existing);
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try { System.Windows.Clipboard.SetFileDropList(list); return true; }
            catch (ExternalException) { await Task.Delay(80); }
        }
        return false;
    }
    private async void TransferCopyFiles(object sender, RoutedEventArgs e)
    { if (sender is Button { DataContext: TransferLog log }) TransferMessage.Text = (log.Text != null ? await CopyTransferTextAsync(log.Text) : await CopyTransferFilesAsync(log.Paths)) ? "已复制到剪贴板" : "内容或剪贴板不可用"; }
    private void TransferOpenFolder(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: TransferLog log }) return;
        if (log.Text != null) { ShowTransferText(log.Text, true); return; }
        if (log.Paths.Length == 0) return;
        var directory = Directory.Exists(log.Paths[0]) ? log.Paths[0] : Path.GetDirectoryName(log.Paths[0]);
        if (directory != null && Directory.Exists(directory)) Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
    }
    private void TransferCopyAddress(object sender, RoutedEventArgs e)
    { if (_transfer == null) return; try { System.Windows.Clipboard.SetText(TransferService.Addresses().FirstOrDefault() + ":" + _transfer.Self.Port); TransferMessage.Text = "已复制本机地址"; } catch (ExternalException) { TransferMessage.Text = "剪贴板暂不可用"; } }
    private void TransferFailure(Exception error)
    {
        SettingsStore.Log(error);
        var root = error.GetBaseException();
        TransferMessage.Text = error is OperationCanceledException ? "传输已取消或请求超时"
            : root is SocketException socket ? socket.SocketErrorCode == SocketError.ConnectionRefused ? "对方接收服务未就绪" : "网络连接失败 · " + socket.SocketErrorCode
            : error is HttpRequestException && (error.Message.Contains("SSL", StringComparison.OrdinalIgnoreCase) || root is System.Security.Authentication.AuthenticationException) ? "加密连接失败 · 对端握手未完成"
            : error is HttpRequestException ? "连接失败 · " + root.Message : error.Message;
    }
    private void TransferNavClick(object sender, RoutedEventArgs e) => Navigate(3);
    private void DisposeTransfer()
    {
        _transferTimer.Stop(); _transferOffer?.Reject(); _transferSend?.Cancel(); _transferPrepare?.Cancel();
        if (_transferSend == null) ClearTransferSelection();
        if (_transfer != null) _ = _transfer.DisposeAsync();
    }
}
