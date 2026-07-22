package com.github.tvbox.osc.ui.activity;

import android.content.Intent;
import android.net.Uri;
import android.view.View;
import android.widget.EditText;
import android.widget.TextView;

import com.github.tvbox.osc.R;
import com.github.tvbox.osc.api.ApiConfig;
import com.github.tvbox.osc.base.BaseActivity;
import com.github.tvbox.osc.membership.MembershipClient;
import com.github.tvbox.osc.util.HawkConfig;
import com.google.gson.JsonArray;
import com.google.gson.JsonElement;
import com.google.gson.JsonObject;
import com.orhanobut.hawk.Hawk;

import java.text.SimpleDateFormat;
import java.util.Date;
import java.util.Locale;
import java.util.UUID;

/** Account UI: cloud upload/download, resource distribution and app update are independent actions. */
public class MembershipActivity extends BaseActivity {
    private static final String CURRENT_VERSION = "1.3.3";
    private static final String CONFIG_ID = "8bb704dd-0884-42e2-97d0-6d8f43b42b79";
    private static final String LIVE_ID = "f26a5aed-b57f-4f4b-b06d-4e20e3f0a2ca";
    private MembershipClient client;
    private EditText server, email, password;
    private TextView status, signedEmail, signedServer;
    private View authForm, signedPanel;

    @Override protected int getLayoutResID() { return R.layout.activity_membership; }

    @Override protected void init() {
        client = new MembershipClient(this);
        server = findViewById(R.id.memberServer); email = findViewById(R.id.memberEmail);
        password = findViewById(R.id.memberPassword); status = findViewById(R.id.memberStatus);
        signedEmail = findViewById(R.id.memberSignedEmail); signedServer = findViewById(R.id.memberSignedServer);
        authForm = findViewById(R.id.memberAuthForm); signedPanel = findViewById(R.id.memberSignedPanel);
        server.setText(client.server());
        updateAccountUi("游客模式：可本地播放；登录后可上传、下载云端配置，并接收会员资源推送。");
        findViewById(R.id.memberLogin).setOnClickListener(v -> login());
        findViewById(R.id.memberRegister).setOnClickListener(v -> register());
        findViewById(R.id.memberUpload).setOnClickListener(v -> upload());
        findViewById(R.id.memberDownload).setOnClickListener(v -> download());
        findViewById(R.id.memberResources).setOnClickListener(v -> receiveResources());
        findViewById(R.id.memberUpdate).setOnClickListener(v -> checkUpdate());
        findViewById(R.id.memberLogout).setOnClickListener(v -> { client.logout(); password.setText(""); updateAccountUi("已退出登录；本地配置和观看记录未删除。"); });
    }

    private void updateAccountUi(String message) {
        boolean signed = client.signedIn();
        authForm.setVisibility(signed ? View.GONE : View.VISIBLE);
        signedPanel.setVisibility(signed ? View.VISIBLE : View.GONE);
        if (signed) {
            signedEmail.setText(client.email());
            signedServer.setText(client.server());
        }
        status.setText(message);
    }

    private void login() {
        execute("正在登录…", () -> {
            client.saveServer(server.getText().toString());
            client.login(text(email), text(password));
            runOnUiThread(() -> updateAccountUi("已登录。可分别选择上传、下载、接收资源推送或检查应用更新。"));
        });
    }

    private void register() {
        execute("正在提交注册…", () -> {
            client.saveServer(server.getText().toString());
            client.register(text(email), text(password));
            show("注册请求已提交；如后台开启邮箱验证，请先完成验证后再登录。");
        });
    }

    private void upload() { execute("正在上传本机配置…", this::uploadInternal); }
    private void download() { execute("正在下载云端配置…", this::downloadInternal); }
    private void receiveResources() { execute("正在接收点播与直播资源推送…", this::receiveResourcesInternal); }
    private void checkUpdate() { execute("正在检查 Android 更新…", this::checkUpdateInternal); }

    private void uploadInternal() throws Exception {
        JsonArray mutations = new JsonArray();
        String api = Hawk.get(HawkConfig.API_URL, "");
        String live = Hawk.get(HawkConfig.LIVE_API_URL, "");
        if (!api.isEmpty()) mutations.add(mutation(CONFIG_ID, "ConfigurationGroup", client.version(CONFIG_ID), payload("name", "MT播放器 Android 点播配置", "address", api)));
        if (!live.isEmpty()) mutations.add(mutation(LIVE_ID, "Preference", client.version(LIVE_ID), payload("key", "liveApiUrl", "value", live)));
        if (mutations.size() == 0) throw new IllegalStateException("当前没有可上传的点播或直播地址");
        JsonArray result = client.syncPush(mutations);
        for (JsonElement value : result) {
            if (!value.isJsonObject()) continue;
            JsonObject item = value.getAsJsonObject();
            if (item.has("accepted") && item.get("accepted").getAsBoolean()) client.saveVersion(string(item, "id"), number(item, "version"));
        }
        show("已上传本机点播和直播配置。");
    }

    private void downloadInternal() throws Exception {
        long cursor = client.cursor(); int applied = 0;
        while (true) {
            JsonObject page = client.syncPull(cursor);
            JsonArray changes = array(page, "changes");
            for (JsonElement value : changes) if (value.isJsonObject() && applyChange(value.getAsJsonObject())) applied++;
            long next = number(page, "cursor");
            if (changes.size() < 500 || next == cursor) { cursor = next; break; }
            cursor = next;
        }
        client.saveCursor(cursor);
        final int count = applied;
        runOnUiThread(() -> ApiConfig.get().loadConfig(false, new ApiConfig.LoadConfigCallback() {
            @Override public void success() { status.setText("已下载云端配置，应用了 " + count + " 项；点播配置已刷新。"); }
            @Override public void error(String value) { status.setText("已下载 " + count + " 项，但点播配置刷新失败：" + value); }
            @Override public void notice(String value) { status.setText("已下载云端配置：" + value); }
        }, MembershipActivity.this));
    }

    private boolean applyChange(JsonObject change) {
        if (change.has("isDeleted") && change.get("isDeleted").getAsBoolean()) return false;
        JsonObject data = change.has("payload") && change.get("payload").isJsonObject() ? change.getAsJsonObject("payload") : new JsonObject();
        String kind = string(change, "kind");
        if ("ConfigurationGroup".equals(kind)) {
            String address = string(data, "address");
            if (!address.isEmpty()) { Hawk.put(HawkConfig.API_URL, address); return true; }
        }
        if ("Preference".equals(kind) && "liveApiUrl".equals(string(data, "key"))) {
            String address = string(data, "value");
            if (!address.isEmpty()) { Hawk.put(HawkConfig.LIVE_API_URL, address); return true; }
        }
        return false;
    }

    private void receiveResourcesInternal() throws Exception {
        JsonArray pushes = client.memberResources();
        int sources = 0, lives = 0;
        for (JsonElement value : pushes) {
            if (!value.isJsonObject()) continue;
            JsonObject push = value.getAsJsonObject();
            for (JsonElement source : array(push, "configurationSources")) {
                if (!source.isJsonObject()) continue;
                String address = string(source.getAsJsonObject(), "address");
                if (!address.isEmpty() && sources++ == 0) Hawk.put(HawkConfig.API_URL, address);
            }
            for (JsonElement source : array(push, "liveSources")) {
                if (!source.isJsonObject()) continue;
                String address = string(source.getAsJsonObject(), "address");
                if (!address.isEmpty() && lives++ == 0) Hawk.put(HawkConfig.LIVE_API_URL, address);
            }
        }
        final int configCount = sources, liveCount = lives;
        runOnUiThread(() -> ApiConfig.get().loadConfig(false, new ApiConfig.LoadConfigCallback() {
            @Override public void success() { status.setText("已接收会员资源：点播 " + configCount + " 个，直播 " + liveCount + " 个。"); }
            @Override public void error(String value) { status.setText("已接收资源，但点播配置刷新失败：" + value); }
            @Override public void notice(String value) { status.setText("已接收会员资源：" + value); }
        }, MembershipActivity.this));
    }

    private void checkUpdateInternal() throws Exception {
        JsonArray updates = client.androidUpdates();
        String version = "", url = "", notes = "";
        for (JsonElement value : updates) {
            if (!value.isJsonObject()) continue;
            JsonObject update = value.getAsJsonObject();
            String candidate = string(update, "androidVersion");
            String candidateUrl = string(update, "androidDownloadUrl");
            if (!candidate.isEmpty() && !candidateUrl.isEmpty() && newer(candidate, CURRENT_VERSION)) {
                version = candidate; url = candidateUrl; notes = string(update, "message"); break;
            }
        }
        if (version.isEmpty()) { show("当前已是最新版本（" + CURRENT_VERSION + "）。"); return; }
        final String latest = version, download = url, releaseNotes = notes;
        runOnUiThread(() -> new android.app.AlertDialog.Builder(MembershipActivity.this)
            .setTitle("发现 MT播放器 Android " + latest)
            .setMessage(releaseNotes.isEmpty() ? "后台已发布新版本。" : releaseNotes)
            .setPositiveButton("下载更新", (d, w) -> startActivity(new Intent(Intent.ACTION_VIEW, Uri.parse(download))))
            .setNegativeButton("稍后", null).show());
    }

    private JsonObject mutation(String id, String kind, long version, JsonObject value) {
        JsonObject result = new JsonObject();
        result.addProperty("id", id); result.addProperty("kind", kind); result.addProperty("baseVersion", version);
        result.addProperty("modifiedAtUtc", timestamp()); result.addProperty("isDeleted", false); result.add("payload", value);
        return result;
    }
    private static JsonObject payload(String... values) { JsonObject result = new JsonObject(); for (int i = 0; i + 1 < values.length; i += 2) result.addProperty(values[i], values[i + 1]); return result; }
    private static String timestamp() { return new SimpleDateFormat("yyyy-MM-dd'T'HH:mm:ss.SSSXXX", Locale.US).format(new Date()); }
    private void execute(String progress, Task task) { status.setText(progress); new Thread(() -> { try { task.run(); } catch (Exception e) { show("操作失败：" + e.getMessage()); } }).start(); }
    private void show(String value) { runOnUiThread(() -> status.setText(value)); }
    private static String text(EditText value) { return value.getText().toString().trim(); }
    private static JsonArray array(JsonObject value, String name) { return value.has(name) && value.get(name).isJsonArray() ? value.getAsJsonArray(name) : new JsonArray(); }
    private static String string(JsonObject value, String name) { return value.has(name) && !value.get(name).isJsonNull() ? value.get(name).getAsString() : ""; }
    private static long number(JsonObject value, String name) { try { return value.has(name) ? value.get(name).getAsLong() : 0L; } catch (Exception ignored) { return 0L; } }
    private static boolean newer(String value, String current) { String[] a = value.split("\\."), b = current.split("\\."); for (int i = 0; i < Math.max(a.length, b.length); i++) { int x = i < a.length ? part(a[i]) : 0, y = i < b.length ? part(b[i]) : 0; if (x != y) return x > y; } return false; }
    private static int part(String value) { try { return Integer.parseInt(value.replaceAll("[^0-9]", "")); } catch (Exception ignored) { return 0; } }
    private interface Task { void run() throws Exception; }
}
