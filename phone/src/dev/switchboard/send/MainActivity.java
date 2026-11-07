package dev.switchboard.send;

import android.Manifest;
import android.app.*;
import android.content.*;
import android.content.pm.PackageManager;
import android.content.res.ColorStateList;
import android.database.Cursor;
import android.graphics.Typeface;
import android.graphics.drawable.GradientDrawable;
import android.graphics.drawable.RippleDrawable;
import android.net.Uri;
import android.os.*;
import android.provider.OpenableColumns;
import android.text.TextUtils;
import android.view.*;
import android.widget.*;
import java.util.*;
import java.util.concurrent.*;

public final class MainActivity extends Activity {
    private static final int PAPER = 0xfff4f2ec, INK = 0xff171714, MUTED = 0xff74746c, RED = 0xffc72d25, LINE = 0xffd5d3ca;
    private final Handler main = new Handler(Looper.getMainLooper());
    private final ExecutorService metadata = Executors.newSingleThreadExecutor();
    private final List<SelectedFile> selected = new ArrayList<>();
    private final List<Button> peerButtons = new ArrayList<>();
    private SendService service;
    private boolean bound, visible, destroyed, loading;
    private int page, selectionRevision;
    private long lastCompleted;
    private String peerSignature = "", historySignature = "";
    private PeerEngine.Peer pendingTarget;
    private PeerEngine.Offer displayedOffer;
    private AlertDialog offerDialog;
    private LinearLayout sendPage, receivePage, deviceList, fileList, history, transferPanel;
    private TextView deviceName, address, status, fileCount, fileSize, transferText, transferPercent;
    private ProgressBar transferBar;
    private Button sendTab, receiveTab, addFiles, addMedia, clearFiles, cancel;
    private CheckBox copy;

    private static final class SelectedFile {
        final Uri uri;
        final PeerEngine.Source source;
        SelectedFile(Uri uri, PeerEngine.Source source) { this.uri = uri; this.source = source; }
    }
    private final Runnable poll = new Runnable() {
        public void run() { if (visible) { render(); main.postDelayed(this, 1500); } }
    };
    private final ServiceConnection connection = new ServiceConnection() {
        public void onServiceConnected(ComponentName name, IBinder binder) {
            service = ((SendService.LocalBinder) binder).get();
            service.changed = () -> { if (visible) render(); };
            service.refresh();
            render();
        }
        public void onServiceDisconnected(ComponentName name) { service = null; render(); }
    };

    @Override public void onCreate(Bundle saved) {
        super.onCreate(saved);
        getWindow().setStatusBarColor(PAPER);
        getWindow().setNavigationBarColor(PAPER);
        LinearLayout root = column();
        root.setBackgroundColor(PAPER);
        root.setOnApplyWindowInsetsListener((view, insets) -> {
            view.setPadding(insets.getSystemWindowInsetLeft(), insets.getSystemWindowInsetTop(), insets.getSystemWindowInsetRight(), insets.getSystemWindowInsetBottom());
            return insets.consumeSystemWindowInsets();
        });
        setContentView(root);

        LinearLayout header = row();
        header.setPadding(dp(20), dp(14), dp(20), dp(10));
        TextView title = text("局域网互传", 24, INK, true);
        header.addView(title, new LinearLayout.LayoutParams(0, -2, 1));
        header.addView(button("设置", false, v -> settings()));
        root.addView(header);
        status = text("启动中", 12, MUTED, false);
        status.setPadding(dp(20), 0, dp(20), dp(12));
        root.addView(status);
        rule(root);

        FrameLayout content = new FrameLayout(this);
        root.addView(content, new LinearLayout.LayoutParams(-1, 0, 1));
        ScrollView sendScroll = scroll();
        sendPage = paddedColumn();
        sendScroll.addView(sendPage);
        content.addView(sendScroll, new FrameLayout.LayoutParams(-1, -1));

        LinearLayout fileHeader = row();
        fileHeader.addView(text("01 / 文件", 12, RED, true), new LinearLayout.LayoutParams(0, -2, 1));
        clearFiles = button("清空", false, v -> { selected.clear(); selectionRevision++; renderFiles(); });
        fileHeader.addView(clearFiles);
        sendPage.addView(fileHeader);
        fileCount = text("0", 44, INK, true);
        sendPage.addView(fileCount);
        fileSize = text("个文件", 12, MUTED, false);
        sendPage.addView(fileSize);
        LinearLayout pickers = row();
        addFiles = button("文件", true, v -> pickFiles(false));
        addMedia = button("照片 / 视频", false, v -> pickFiles(true));
        pickers.addView(addFiles, weighted());
        LinearLayout.LayoutParams second = weighted(); second.leftMargin = dp(10);
        pickers.addView(addMedia, second);
        margin(pickers, 14, 10);
        sendPage.addView(pickers);
        fileList = column();
        sendPage.addView(fileList);

        transferPanel = column();
        transferPanel.setPadding(dp(14), dp(14), dp(14), dp(14));
        transferPanel.setBackgroundColor(0xffe9e6dc);
        LinearLayout transferHeader = row();
        transferText = text("", 14, INK, true);
        transferText.setMaxLines(2);
        transferHeader.addView(transferText, new LinearLayout.LayoutParams(0, -2, 1));
        transferPercent = text("", 18, RED, true);
        transferHeader.addView(transferPercent);
        transferPanel.addView(transferHeader);
        transferBar = new ProgressBar(this, null, android.R.attr.progressBarStyleHorizontal);
        transferBar.setProgressTintList(ColorStateList.valueOf(RED));
        margin(transferBar, 12, 6);
        transferPanel.addView(transferBar);
        cancel = button("取消传输", false, v -> { if (service != null) { if (service.direction.equals("接收") && service.receiving) service.cancelReceive(); else service.cancel(); } });
        transferPanel.addView(cancel);
        margin(transferPanel, 10, 12);
        root.addView(transferPanel);
        transferPanel.setVisibility(View.GONE);

        LinearLayout nearbyHeader = row();
        nearbyHeader.addView(text("02 / 附近设备", 12, RED, true), new LinearLayout.LayoutParams(0, -2, 1));
        nearbyHeader.addView(button("刷新", false, v -> { if (service != null) service.refresh(); }));
        nearbyHeader.addView(button("地址", false, v -> manualPeer()));
        margin(nearbyHeader, 16, 10);
        sendPage.addView(nearbyHeader);
        deviceList = column();
        sendPage.addView(deviceList);

        ScrollView receiveScroll = scroll();
        receiveScroll.setTag("receive");
        receivePage = paddedColumn();
        receiveScroll.addView(receivePage);
        content.addView(receiveScroll, new FrameLayout.LayoutParams(-1, -1));
        receiveScroll.setVisibility(View.GONE);
        receivePage.addView(text("01 / 本机", 12, RED, true));
        deviceName = text(Build.MODEL, 30, INK, true);
        margin(deviceName, 12, 8);
        receivePage.addView(deviceName);
        address = text("启动中", 13, MUTED, false);
        address.setTextIsSelectable(true);
        receivePage.addView(address);
        Button copyAddress = button("复制地址", false, v -> {
            if (service == null || service.addresses.isEmpty()) return;
            getSystemService(ClipboardManager.class).setPrimaryClip(ClipData.newPlainText("设备地址", service.addresses.split("\n")[0]));
            toast("已复制地址");
        });
        margin(copyAddress, 10, 16);
        receivePage.addView(copyAddress);
        copy = new CheckBox(this);
        copy.setButtonDrawable(R.drawable.checkbox_square); copy.setButtonTintList(null);
        copy.setText("接收后复制到剪贴板"); copy.setTextSize(14); copy.setTextColor(INK);
        copy.setChecked(getSharedPreferences("send", 0).getBoolean("copy", false));
        copy.setOnCheckedChangeListener((view, checked) -> getSharedPreferences("send", 0).edit().putBoolean("copy", checked).apply());
        receivePage.addView(copy);
        TextView directory = text("Downloads / ToolHub Send", 12, MUTED, false);
        margin(directory, 6, 24); receivePage.addView(directory);
        rule(receivePage);
        TextView records = text("02 / 接收记录", 12, RED, true);
        margin(records, 20, 12); receivePage.addView(records);
        history = column(); receivePage.addView(history);

        rule(root);
        LinearLayout tabs = row(); tabs.setPadding(dp(16), dp(6), dp(16), dp(6));
        sendTab = button("↑  发送", true, v -> showPage(0));
        receiveTab = button("↓  接收", false, v -> showPage(1));
        tabs.addView(sendTab, weighted()); tabs.addView(receiveTab, weighted()); root.addView(tabs);
        sendTab.setTag(sendScroll); receiveTab.setTag(receiveScroll);

        if (saved != null) {
            page = saved.getInt("page"); lastCompleted = saved.getLong("completed");
            ArrayList<String> values = saved.getStringArrayList("files");
            if (values != null) { List<Uri> uris = new ArrayList<>(); for (String value : values) uris.add(Uri.parse(value)); loadFiles(uris); }
        } else readShared(getIntent());
        showPage(page);
        renderFiles();
        if (Build.VERSION.SDK_INT >= 33 && checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED
            && !getPreferences(0).getBoolean("notificationAsked", false)) {
            getPreferences(0).edit().putBoolean("notificationAsked", true).apply();
            requestPermissions(new String[]{Manifest.permission.POST_NOTIFICATIONS}, 3);
        }
    }
    @Override protected void onStart() {
        super.onStart(); visible = true;
        startForegroundService(new Intent(this, SendService.class).setAction("resume"));
        if (!bound) bound = bindService(new Intent(this, SendService.class), connection, BIND_AUTO_CREATE);
        if (service != null) { service.changed = () -> { if (visible) render(); }; service.refresh(); }
        main.post(poll);
    }
    @Override protected void onStop() { visible = false; main.removeCallbacks(poll); dismissOffer(); super.onStop(); }
    @Override protected void onDestroy() {
        destroyed = true;
        if (service != null) service.changed = null;
        if (bound) unbindService(connection);
        metadata.shutdownNow();
        super.onDestroy();
    }
    @Override protected void onSaveInstanceState(Bundle out) {
        ArrayList<String> values = new ArrayList<>(); for (SelectedFile file : selected) values.add(file.uri.toString());
        out.putStringArrayList("files", values); out.putInt("page", page); out.putLong("completed", lastCompleted);
        super.onSaveInstanceState(out);
    }
    @Override protected void onNewIntent(Intent intent) { super.onNewIntent(intent); readShared(intent); }
    private void readShared(Intent intent) {
        List<Uri> uris = new ArrayList<>();
        if (Intent.ACTION_SEND.equals(intent.getAction())) { Uri value = intent.getParcelableExtra(Intent.EXTRA_STREAM); if (value != null) uris.add(value); }
        if (Intent.ACTION_SEND_MULTIPLE.equals(intent.getAction())) { ArrayList<Uri> values = intent.getParcelableArrayListExtra(Intent.EXTRA_STREAM); if (values != null) uris.addAll(values); }
        if (!uris.isEmpty()) { showPage(0); loadFiles(uris); }
    }
    private void showPage(int value) {
        page = value;
        ((View) sendTab.getTag()).setVisibility(value == 0 ? View.VISIBLE : View.GONE);
        ((View) receiveTab.getTag()).setVisibility(value == 1 ? View.VISIBLE : View.GONE);
        tint(sendTab, value == 0); tint(receiveTab, value == 1);
    }
    private void pickFiles(boolean media) {
        Intent intent = new Intent(Intent.ACTION_OPEN_DOCUMENT).setType("*/*").addCategory(Intent.CATEGORY_OPENABLE).putExtra(Intent.EXTRA_ALLOW_MULTIPLE, true);
        if (media) intent.putExtra(Intent.EXTRA_MIME_TYPES, new String[]{"image/*", "video/*"});
        startActivityForResult(intent, 1);
    }
    @Override protected void onActivityResult(int request, int result, Intent data) {
        super.onActivityResult(request, result, data);
        if (request != 1) return;
        if (result != RESULT_OK || data == null) { pendingTarget = null; return; }
        List<Uri> uris = new ArrayList<>();
        ClipData clip = data.getClipData();
        if (clip != null) for (int i = 0; i < clip.getItemCount(); i++) uris.add(clip.getItemAt(i).getUri());
        else if (data.getData() != null) uris.add(data.getData());
        for (Uri uri : uris) try { getContentResolver().takePersistableUriPermission(uri, data.getFlags() & Intent.FLAG_GRANT_READ_URI_PERMISSION); } catch (Exception ignored) { }
        loadFiles(uris);
    }
    private void loadFiles(List<Uri> values) {
        if (values.isEmpty()) return;
        loading = true; render();
        final int revision = selectionRevision;
        metadata.execute(() -> {
            List<SelectedFile> result = new ArrayList<>();
            for (Uri uri : values) {
                if (result.size() >= 512) break;
                if (uri == null || !"content".equals(uri.getScheme())) continue;
                try { result.add(describe(uri)); } catch (Exception ignored) { }
            }
            main.post(() -> {
                if (destroyed) return;
                loading = false;
                if (revision == selectionRevision) {
                    for (SelectedFile file : result) {
                        boolean exists = false; for (SelectedFile prior : selected) if (prior.uri.equals(file.uri)) exists = true;
                        if (!exists && selected.size() < 512) selected.add(file);
                    }
                }
                renderFiles();
                PeerEngine.Peer target = pendingTarget; pendingTarget = null;
                if (target != null && !selected.isEmpty()) sendTo(target);
            });
        });
    }
    private SelectedFile describe(Uri uri) throws Exception {
        ContentResolver resolver = getApplicationContext().getContentResolver();
        String name = "file", mime = resolver.getType(uri); long size = -1;
        try (Cursor cursor = resolver.query(uri, new String[]{OpenableColumns.DISPLAY_NAME, OpenableColumns.SIZE}, null, null, null)) {
            if (cursor != null && cursor.moveToFirst()) { name = cursor.getString(0); if (!cursor.isNull(1)) size = cursor.getLong(1); }
        }
        return new SelectedFile(uri, new PeerEngine.Source(name == null ? "file" : name, mime == null ? "application/octet-stream" : mime, size, () -> resolver.openInputStream(uri)));
    }
    private void renderFiles() {
        if (fileList == null) return;
        fileCount.setText(String.format(Locale.ROOT, "%02d", selected.size()));
        long bytes = 0; for (SelectedFile file : selected) if (file.source.size > 0) bytes += file.source.size;
        fileSize.setText("个文件" + (bytes > 0 ? " · " + size(bytes) : ""));
        fileList.removeAllViews();
        for (int i = 0; i < Math.min(3, selected.size()); i++) {
            SelectedFile file = selected.get(i);
            TextView label = text(file.source.name, 13, INK, false);
            label.setMaxLines(1); label.setEllipsize(TextUtils.TruncateAt.MIDDLE); label.setPadding(0, dp(7), 0, dp(7));
            label.setOnClickListener(v -> fileSelection()); fileList.addView(label);
        }
        if (selected.size() > 3) fileList.addView(button("全部文件 · " + selected.size(), false, v -> fileSelection()));
        render();
    }
    private void fileSelection() {
        if (service != null && service.sending) return;
        String[] names = new String[selected.size()]; boolean[] keep = new boolean[selected.size()]; Arrays.fill(keep, true);
        for (int i = 0; i < names.length; i++) names[i] = selected.get(i).source.name;
        new AlertDialog.Builder(this).setTitle("已选文件").setMultiChoiceItems(names, keep, (d, index, checked) -> keep[index] = checked)
            .setNegativeButton("取消", null).setPositiveButton("完成", (d, w) -> { for (int i = keep.length - 1; i >= 0; i--) if (!keep[i]) selected.remove(i); selectionRevision++; renderFiles(); }).show();
    }
    private void render() {
        if (destroyed || status == null) return;
        SendService current = service;
        boolean sending = current != null && current.sending;
        addFiles.setEnabled(!sending && !loading); addMedia.setEnabled(!sending && !loading); clearFiles.setEnabled(!sending && !loading && !selected.isEmpty());
        if (current == null) return;
        update(status, loading ? "读取文件中" : current.status);
        update(deviceName, current.engine == null ? getSharedPreferences("send", 0).getString("name", Build.MODEL) : current.engine.self.name);
        update(address, current.addresses.isEmpty() ? "无局域网地址" : current.addresses);
        transferPanel.setVisibility(sending || current.receiving ? View.VISIBLE : View.GONE);
        update(transferText, current.direction + " · " + current.progress); update(transferPercent, current.percent + "%"); transferBar.setProgress(current.percent);
        transferBar.setIndeterminate(sending && (current.progress.contains("校验中") || current.progress.contains("等待接收") || current.progress.startsWith("连接")));
        cancel.setEnabled(sending || current.receiving);
        if (!sending && current.completedSerial > lastCompleted) {
            lastCompleted = current.completedSerial;
            if (current.lastSendSucceeded) { selected.clear(); selectionRevision++; renderFiles(); toast(current.status); }
        }
        List<PeerEngine.Peer> peers = current.engine == null ? Collections.emptyList() : current.engine.peers();
        StringBuilder signature = new StringBuilder();
        for (PeerEngine.Peer peer : peers) signature.append(peer.id).append(peer.name).append(peer.platform).append(peer.address).append(peer.port).append(peer.pin);
        if (!signature.toString().equals(peerSignature) || deviceList.getChildCount() == 0) {
            peerSignature = signature.toString(); deviceList.removeAllViews(); peerButtons.clear();
            for (PeerEngine.Peer peer : peers) {
                Button item = button(peer.name + "\n" + peer.platform + " · " + peer.address + "    ↗", false, v -> {
                    if (loading || service == null || service.sending) return;
                    if (selected.isEmpty()) { pendingTarget = peer; pickFiles(false); } else sendTo(peer);
                });
                item.setGravity(Gravity.LEFT | Gravity.CENTER_VERTICAL); item.setMinHeight(dp(76));
                item.setBackgroundTintList(null);
                item.setBackground(new RippleDrawable(ColorStateList.valueOf(0x22c72d25), outline(), null));
                margin(item, 0, 10); deviceList.addView(item); peerButtons.add(item);
            }
            if (peers.isEmpty()) { TextView empty = text("暂无设备", 14, MUTED, false); margin(empty, 14, 14); deviceList.addView(empty); }
        }
        for (Button item : peerButtons) item.setEnabled(!sending && !loading);
        renderHistory(current);
        renderOffer(current.pending);
        if (sending || current.receiving) getWindow().addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON); else getWindow().clearFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
    }
    private void renderHistory(SendService current) {
        List<PeerEngine.Item> items; synchronized (current.history) { items = new ArrayList<>(current.history); }
        StringBuilder signature = new StringBuilder(); for (PeerEngine.Item item : items) signature.append(item.uri);
        if (signature.toString().equals(historySignature) && history.getChildCount() > 0) return;
        historySignature = signature.toString(); history.removeAllViews();
        if (items.isEmpty()) history.addView(text("暂无记录", 14, MUTED, false));
        for (PeerEngine.Item item : items) {
            TextView label = text(item.name, 15, INK, true); label.setMaxLines(2); margin(label, 12, 4); history.addView(label);
            history.addView(text(size(item.size), 12, MUTED, false));
            LinearLayout actions = row();
            actions.addView(button("打开", false, v -> open(item)));
            actions.addView(button("分享", false, v -> share(item)));
            actions.addView(button("复制", false, v -> { current.copy(Collections.singletonList(item)); toast(current.status); }));
            margin(actions, 4, 12); history.addView(actions); rule(history);
        }
    }
    private void renderOffer(PeerEngine.Offer offer) {
        if (!visible) return;
        if (offer == null || !offer.pending()) { dismissOffer(); return; }
        if (displayedOffer == offer && offerDialog != null) return;
        dismissOffer(); displayedOffer = offer;
        StringBuilder message = new StringBuilder(offer.count + " 个文件 · " + size(offer.bytes));
        for (int i = 0; i < Math.min(4, offer.names.size()); i++) message.append("\n").append(offer.names.get(i));
        if (offer.names.size() > 4) message.append("\n…");
        offerDialog = new AlertDialog.Builder(this).setTitle(offer.peer).setMessage(message.toString()).setCancelable(false)
            .setNegativeButton("拒绝", (d, w) -> { if (service != null) service.reject(); })
            .setPositiveButton("接收", (d, w) -> { if (service != null) service.accept(); showPage(1); }).create();
        offerDialog.show();
    }
    private void dismissOffer() { if (offerDialog != null) { offerDialog.dismiss(); offerDialog = null; } displayedOffer = null; }
    private void sendTo(PeerEngine.Peer peer) {
        if (service == null || service.sending || loading || selected.isEmpty()) return;
        List<PeerEngine.Source> sources = new ArrayList<>(); for (SelectedFile file : selected) sources.add(file.source);
        showPage(0); service.send(peer, sources); render();
    }
    private void manualPeer() {
        EditText field = input("192.168.1.10:53318"); field.setInputType(android.text.InputType.TYPE_CLASS_TEXT | android.text.InputType.TYPE_TEXT_VARIATION_URI);
        new AlertDialog.Builder(this).setTitle("设备地址").setView(field).setNegativeButton("取消", null).setPositiveButton("连接", (d, w) -> { if (service != null) service.addPeer(field.getText().toString()); }).show();
    }
    private void settings() {
        LinearLayout body = paddedColumn();
        body.addView(text("设备名称", 12, MUTED, false));
        EditText name = input(""); name.setText(deviceName.getText()); name.setFilters(new android.text.InputFilter[]{new android.text.InputFilter.LengthFilter(64)}); body.addView(name);
        TextView version = text("ToolHub Send / 0.4.3", 12, MUTED, false); margin(version, 14, 10); body.addView(version);
        if (service != null && !service.detail.isEmpty()) { TextView detail = text(service.detail, 12, RED, false); detail.setTextIsSelectable(true); body.addView(detail); }
        new AlertDialog.Builder(this).setTitle("设置").setView(body).setNegativeButton("取消", null).setPositiveButton("保存", (d, w) -> { if (service != null) service.setName(name.getText().toString()); }).show();
    }
    private void open(PeerEngine.Item item) {
        try { startActivity(new Intent(Intent.ACTION_VIEW).setDataAndType(Uri.parse(item.uri), item.mime).addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)); }
        catch (Exception error) { toast("没有可打开此文件的应用"); }
    }
    private void share(PeerEngine.Item item) {
        Intent intent = new Intent(Intent.ACTION_SEND).setType(item.mime).putExtra(Intent.EXTRA_STREAM, Uri.parse(item.uri))
            .addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION);
        intent.setClipData(ClipData.newRawUri(item.name, Uri.parse(item.uri)));
        startActivity(Intent.createChooser(intent, "文件分享"));
    }
    private void toast(String message) { Toast.makeText(this, message, Toast.LENGTH_SHORT).show(); }
    private static void update(TextView view, String value) { if (!value.contentEquals(view.getText())) view.setText(value); }
    private EditText input(String hint) { EditText field = new EditText(this); field.setSingleLine(true); field.setHint(hint); field.setPadding(dp(20), dp(12), dp(20), dp(12)); return field; }
    private ScrollView scroll() { ScrollView view = new ScrollView(this); view.setFillViewport(true); return view; }
    private LinearLayout paddedColumn() { LinearLayout view = column(); view.setPadding(dp(20), dp(16), dp(20), dp(24)); return view; }
    private LinearLayout column() { LinearLayout view = new LinearLayout(this); view.setOrientation(LinearLayout.VERTICAL); return view; }
    private LinearLayout row() { LinearLayout view = new LinearLayout(this); view.setOrientation(LinearLayout.HORIZONTAL); view.setGravity(Gravity.CENTER_VERTICAL); return view; }
    private TextView text(String value, int sp, int color, boolean bold) { TextView view = new TextView(this); view.setText(value); view.setTextColor(color); view.setTextSize(sp); if (bold) view.setTypeface(Typeface.create("sans-serif", Typeface.BOLD)); return view; }
    private Button button(String value, boolean primary, View.OnClickListener action) { Button view = new Button(this); view.setText(value); view.setTextSize(14); view.setAllCaps(false); view.setMinHeight(dp(48)); view.setMinimumHeight(dp(48)); view.setMinWidth(dp(64)); view.setPadding(dp(14), dp(8), dp(14), dp(8)); tint(view, primary); view.setOnClickListener(action); return view; }
    private void tint(Button view, boolean primary) { view.setTextColor(new ColorStateList(new int[][]{new int[]{-android.R.attr.state_enabled}, new int[]{}}, new int[]{MUTED, primary ? 0xffffffff : INK})); view.setBackgroundTintList(ColorStateList.valueOf(primary ? RED : 0xffe6e3da)); }
    private GradientDrawable outline() { GradientDrawable background = new GradientDrawable(); background.setColor(PAPER); background.setStroke(dp(1), LINE); background.setCornerRadius(0); return background; }
    private void rule(LinearLayout parent) { View view = new View(this); view.setBackgroundColor(LINE); parent.addView(view, new LinearLayout.LayoutParams(-1, dp(1))); }
    private void margin(View view, int top, int bottom) { LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(-1, -2); lp.topMargin = dp(top); lp.bottomMargin = dp(bottom); view.setLayoutParams(lp); }
    private LinearLayout.LayoutParams weighted() { return new LinearLayout.LayoutParams(0, -2, 1); }
    private int dp(int value) { return (int) (value * getResources().getDisplayMetrics().density + 0.5f); }
    static String size(long n) { if (n < 1024) return n + " B"; if (n < 1048576) return String.format(Locale.ROOT, "%.1f KB", n / 1024d); if (n < 1073741824L) return String.format(Locale.ROOT, "%.1f MB", n / 1048576d); return String.format(Locale.ROOT, "%.1f GB", n / 1073741824d); }
}
