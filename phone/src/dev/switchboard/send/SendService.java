package dev.switchboard.send;

import android.app.*;
import android.content.*;
import android.net.*;
import android.net.wifi.WifiManager;
import android.os.*;
import android.util.Log;
import java.util.*;
import java.util.concurrent.*;
import java.util.concurrent.atomic.AtomicBoolean;
import org.json.*;

public final class SendService extends Service implements PeerEngine.Events {
    private static final String CHANNEL = "transfer";
    private static final int NOTIFICATION = 11, INCOMING = 12;
    public final class LocalBinder extends Binder { SendService get() { return SendService.this; } }
    private final LocalBinder binder = new LocalBinder();
    private final Handler main = new Handler(Looper.getMainLooper());
    private final ExecutorService control = Executors.newSingleThreadExecutor();
    private final ExecutorService sender = Executors.newSingleThreadExecutor();
    private final ScheduledExecutorService network = Executors.newSingleThreadScheduledExecutor();
    private final AtomicBoolean uiQueued = new AtomicBoolean();
    private WifiManager.MulticastLock multicast;
    private ConnectivityManager.NetworkCallback networkCallback;
    private volatile boolean closed, starting, receivingWanted = true;
    private Future<?> sendJob;
    private long sendSerial;

    public volatile PeerEngine engine;
    public volatile PeerEngine.Offer pending;
    public volatile String status = "启动中", detail = "", addresses = "", progress = "", direction = "";
    public volatile int percent;
    public volatile boolean sending, receiving, lastSendSucceeded;
    public volatile long completedSerial;
    public final List<PeerEngine.Item> history = new ArrayList<>();
    public Runnable changed;

    @Override public void onCreate() {
        super.onCreate();
        NotificationManager manager = getSystemService(NotificationManager.class);
        manager.createNotificationChannel(new NotificationChannel(CHANNEL, "局域网文件传输", NotificationManager.IMPORTANCE_LOW));
        startForeground(NOTIFICATION, notification("局域网互传运行中").build());
        loadHistory();
        startListener();
        network.scheduleWithFixedDelay(this::updateNetwork, 0, 5, TimeUnit.SECONDS);
        networkCallback = new ConnectivityManager.NetworkCallback() {
            @Override public void onAvailable(Network value) { refresh(); }
            @Override public void onLost(Network value) { refresh(); }
            @Override public void onLinkPropertiesChanged(Network value, LinkProperties properties) { refresh(); }
        };
        getSystemService(ConnectivityManager.class).registerDefaultNetworkCallback(networkCallback);
    }

    private synchronized void startListener() {
        if (closed || starting || engine != null) return;
        receivingWanted = true;
        starting = true;
        status = "启动中";
        control.execute(() -> {
            PeerEngine candidate = null;
            try {
                AndroidIdentity identity = new AndroidIdentity();
                SharedPreferences prefs = getSharedPreferences("send", 0);
                String id = prefs.getString("id", null);
                if (id == null) { id = UUID.randomUUID().toString(); prefs.edit().putString("id", id).apply(); }
                candidate = new PeerEngine(id, prefs.getString("name", Build.MODEL), "Android", identity.fingerprint,
                    identity.tls, new AndroidStorage(this), this, PeerEngine.PORT, "0.0.0.0", true);
                if (closed || !receivingWanted) { candidate.close(); return; }
                WifiManager wifi = (WifiManager) getApplicationContext().getSystemService(WIFI_SERVICE);
                if (wifi != null && multicast == null) {
                    multicast = wifi.createMulticastLock("switchboard-send");
                    multicast.setReferenceCounted(false);
                }
                if (multicast != null && !multicast.isHeld()) multicast.acquire();
                candidate.start();
                candidate.checkListener();
                if (closed || !receivingWanted) { candidate.close(); releaseMulticast(); return; }
                engine = candidate;
                status = "接收就绪";
                detail = "";
                updateNetwork();
            } catch (Exception error) {
                if (candidate != null) candidate.close();
                releaseMulticast();
                failure("接收服务启动失败", error);
            } finally {
                starting = false;
                notifyUi();
            }
        });
    }

    @Override public IBinder onBind(Intent intent) { return binder; }
    @Override public int onStartCommand(Intent intent, int flags, int startId) {
        String action = intent == null ? "" : intent.getAction();
        if ("accept".equals(action)) accept();
        else if ("reject".equals(action)) reject();
        else if ("stop".equals(action)) { stopReceiving(); stopSelf(); }
        else if ("resume".equals(action)) {
            startForeground(NOTIFICATION, notification("局域网互传运行中").build());
            startListener();
            refresh();
        }
        return START_NOT_STICKY;
    }
    @Override public void onTimeout(int startId, int fgsType) { stopReceiving(); stopSelf(); }
    private void stopReceiving() {
        receivingWanted = false;
        receiving = false;
        cancel();
        reject();
        PeerEngine active = engine;
        engine = null;
        if (active != null) active.close();
        releaseMulticast();
        status = "接收已停止";
        stopForeground(STOP_FOREGROUND_REMOVE);
        notifyUi();
    }
    private void releaseMulticast() { if (multicast != null && multicast.isHeld()) multicast.release(); }
    public void refresh() { if (!closed) try { network.execute(this::updateNetwork); } catch (RejectedExecutionException ignored) { } }
    private void updateNetwork() {
        if (closed) return;
        PeerEngine active = engine;
        List<String> current = PeerEngine.addresses();
        String value = String.join("\n", current);
        if (active != null) {
            List<String> withPorts = new ArrayList<>();
            for (String address : current) withPorts.add(address + ":" + active.self.port);
            value = String.join("\n", withPorts);
            try { active.refreshDiscovery(); } catch (Exception error) { Log.w("Switchboard", "设备发现刷新失败", error); }
        }
        if (!value.equals(addresses)) { addresses = value; notifyUi(); }
    }
    private Notification.Builder notification(String text) {
        PendingIntent open = PendingIntent.getActivity(this, 0, new Intent(this, MainActivity.class), PendingIntent.FLAG_IMMUTABLE | PendingIntent.FLAG_UPDATE_CURRENT);
        return new Notification.Builder(this, CHANNEL).setSmallIcon(R.drawable.ic_notification).setContentTitle("ToolHub Send")
            .setContentText(text).setContentIntent(open).setOngoing(true)
            .addAction(new Notification.Action.Builder(null, "停止", action("stop", 3)).build());
    }
    private PendingIntent action(String name, int code) {
        return PendingIntent.getService(this, code, new Intent(this, SendService.class).setAction(name), PendingIntent.FLAG_IMMUTABLE | PendingIntent.FLAG_UPDATE_CURRENT);
    }
    private void notifyUi() {
        if (closed || !uiQueued.compareAndSet(false, true)) return;
        main.post(() -> { uiQueued.set(false); if (!closed && changed != null) changed.run(); });
    }
    @Override public void offer(PeerEngine.Offer offer) {
        pending = offer;
        notifyUi();
        main.postDelayed(() -> { if (pending == offer && !offer.pending()) { pending = null; getSystemService(NotificationManager.class).cancel(INCOMING); notifyUi(); } }, 121000);
        main.post(() -> {
            if (closed || !offer.pending()) return;
            Notification notification = notification(offer.peer + " · " + offer.count + " 项内容").setOngoing(false)
                .addAction(new Notification.Action.Builder(null, "接收", offer.folder&&AndroidPayloads.receiveTree(this)==null?PendingIntent.getActivity(this,3,new Intent(this,MainActivity.class),PendingIntent.FLAG_IMMUTABLE|PendingIntent.FLAG_UPDATE_CURRENT):action("accept", 1)).build())
                .addAction(new Notification.Action.Builder(null, "拒绝", action("reject", 2)).build()).build();
            getSystemService(NotificationManager.class).notify(INCOMING, notification);
        });
    }
    public void accept() {
        PeerEngine.Offer offer = pending;
        if(offer!=null&&offer.folder&&AndroidPayloads.receiveTree(this)==null){status="文件夹接收目录未设置";notifyUi();startActivity(new Intent(this,MainActivity.class).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK));return;}
        if (offer != null) { receiving = true; direction = "接收"; percent = 0; progress = offer.peer + " · 等待内容"; offer.accept(copyEnabled()); } clearOffer();
    }
    public void reject() { PeerEngine.Offer offer = pending; if (offer != null) offer.reject(); clearOffer(); }
    private void clearOffer() { pending = null; getSystemService(NotificationManager.class).cancel(INCOMING); notifyUi(); }
    public boolean copyEnabled() { return getSharedPreferences("send", 0).getBoolean("copy", false); }
    public void setCopy(boolean value) { getSharedPreferences("send", 0).edit().putBoolean("copy", value).apply(); }
    public void setName(String value) {
        String name = value.trim();
        if (name.isEmpty()) return;
        if (name.length() > 64) name = name.substring(0, 64);
        getSharedPreferences("send", 0).edit().putString("name", name).apply();
        if (engine != null) engine.self.name = name;
        refresh();
        notifyUi();
    }
    public void addPeer(String address) {
        status = "连接中";
        notifyUi();
        control.execute(() -> {
            try {
                PeerEngine active = engine;
                if (active == null) throw new IllegalStateException("接收服务未就绪");
                PeerEngine.Peer peer = active.addPeer(address);
                status = "已连接 · " + peer.name;
                detail = "";
            } catch (Exception error) { failure("设备连接失败", error); }
            notifyUi();
        });
    }
    public synchronized void send(PeerEngine.Peer peer, List<PeerEngine.Source> sources) {
        if (sending || engine == null || sources.isEmpty()) return;
        final PeerEngine active = engine;
        final List<PeerEngine.Source> snapshot = new ArrayList<>(sources);
        final long ticket = ++sendSerial;
        sending = true;
        lastSendSucceeded = false;
        direction = "发送";
        percent = 0;
        progress = "连接 · " + peer.name;
        notifyUi();
        FutureTask<Void> job = new FutureTask<Void>(() -> {
            try {
                active.send(peer, snapshot);
                status = "已发送 · " + snapshot.size() + " 项内容";
                detail = "";
                lastSendSucceeded = true;
            } catch (Exception error) { if (Thread.currentThread().isInterrupted()) status = "已取消"; else failure("发送失败", error); }
            return null;
        }) {
            @Override protected void done() {
                synchronized (SendService.this) {
                    if (ticket == sendSerial) { sending = false; completedSerial = ticket; }
                }
                notifyUi();
            }
        };
        sendJob = job;
        sender.execute(job);
    }
    public synchronized void cancel() {
        if (engine != null) engine.cancelSend();
        if (sendJob != null && sending) { sendJob.cancel(true); status = "已取消"; lastSendSucceeded = false; }
        notifyUi();
    }
    public void cancelReceive() { if (engine != null) engine.cancelReceive(); receiving = false; clearOffer(); status = "接收已取消"; notifyUi(); }
    @Override public void received(String peer, List<PeerEngine.Item> items, boolean copy) {
        main.post(() -> {
            if (closed) return;
            receiving = false;
            synchronized (history) { history.addAll(0, items); while (history.size() > 40) history.remove(history.size() - 1); }
            saveHistory();
            status = "已接收 · " + items.size() + " 项内容";
            if (copy) copy(items);
            notifyUi();
        });
    }
    public void copy(List<PeerEngine.Item> items) {
        if (items.isEmpty()) return;
        try {
            ClipData data = items.size()==1&&"text".equals(items.get(0).kind)?ClipData.newPlainText("文本",AndroidPayloads.readText(this,items.get(0))):ClipData.newUri(getContentResolver(), "ToolHub 文件", Uri.parse(items.get(0).uri));
            for (int i = 1; i < items.size(); i++) data.addItem(new ClipData.Item(Uri.parse(items.get(i).uri)));
            getSystemService(ClipboardManager.class).setPrimaryClip(data);
            status = "已复制到剪贴板";
        } catch (Exception error) { failure("剪贴板不可用", error); }
        notifyUi();
    }
    @Override public void progress(String direction, String peer, String file, long completed, long total) {
        this.direction = direction;
        if (direction.equals("接收")) receiving = !file.equals("已完成");
        progress = peer + " · " + file;
        percent = total > 0 ? (int) Math.min(100, completed * 100 / total) : 0;
        notifyUi();
    }
    @Override public void notice(String text) { status = text; if (text.startsWith("接收失败") || text.startsWith("接收已超时")) receiving = false; Log.w("Switchboard", text); notifyUi(); }
    private void failure(String title, Exception error) {
        Log.w("Switchboard", title, error);
        Throwable cause = error;
        while (cause.getCause() != null) cause = cause.getCause();
        detail = cause.getClass().getSimpleName() + ": " + String.valueOf(cause.getMessage());
        status = error instanceof javax.net.ssl.SSLException ? "加密连接失败" : title + " · " + String.valueOf(error.getMessage());
    }
    private void saveHistory() {
        try {
            JSONArray list = new JSONArray();
            synchronized (history) { for (PeerEngine.Item item : history) list.put(new JSONObject().put("name", item.name).put("uri", item.uri).put("mime", item.mime).put("size", item.size).put("kind",item.kind)); }
            getSharedPreferences("send", 0).edit().putString("history", list.toString()).apply();
        } catch (Exception error) { Log.w("Switchboard", "记录保存失败", error); }
    }
    private void loadHistory() {
        try {
            JSONArray list = new JSONArray(getSharedPreferences("send", 0).getString("history", "[]"));
            for (int i = 0; i < list.length(); i++) {
                JSONObject item = list.getJSONObject(i);
                history.add(new PeerEngine.Item(item.getString("name"), item.getString("uri"), item.getString("mime"), item.getLong("size"),item.optString("kind","file")));
            }
        } catch (Exception error) { Log.w("Switchboard", "记录读取失败", error); }
    }
    @Override public void onDestroy() {
        closed = true;
        changed = null;
        cancel();
        reject();
        if (engine != null) engine.close();
        releaseMulticast();
        if (networkCallback != null) getSystemService(ConnectivityManager.class).unregisterNetworkCallback(networkCallback);
        control.shutdownNow();
        sender.shutdownNow();
        network.shutdownNow();
        getSystemService(NotificationManager.class).cancel(INCOMING);
        super.onDestroy();
    }
}
