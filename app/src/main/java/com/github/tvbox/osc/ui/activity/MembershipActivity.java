package com.github.tvbox.osc.ui.activity;

import android.content.Intent;
import android.net.Uri;
import android.view.View;
import android.widget.Button;
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

/** Account login and member push receiver for the MT-branded TVBoxOS build. */
public class MembershipActivity extends BaseActivity {
    private MembershipClient client;
    private EditText server, email, password;
    private TextView status;

    @Override protected int getLayoutResID() { return R.layout.activity_membership; }

    @Override protected void init() {
        client = new MembershipClient(this);
        server = findViewById(R.id.memberServer); email = findViewById(R.id.memberEmail);
        password = findViewById(R.id.memberPassword); status = findViewById(R.id.memberStatus);
        server.setText(client.server());
        status.setText(client.signedIn() ? "已登录。单击即可检查会员推送和版本更新。" : "游客模式：本地播放与配置导入可用，但不会接收会员推送。 ");
        findViewById(R.id.memberLogin).setOnClickListener(v -> login());
        findViewById(R.id.memberRegister).setOnClickListener(v -> register());
        findViewById(R.id.memberRefresh).setOnClickListener(v -> receivePushes());
        findViewById(R.id.memberLogout).setOnClickListener(v -> { client.logout(); status.setText("已退出登录；本地配置和观看记录未删除。"); });
    }

    private void login() {
        execute(() -> { client.saveServer(server.getText().toString()); client.login(text(email), text(password)); receivePushesInternal(); });
    }

    private void register() {
        execute(() -> { client.saveServer(server.getText().toString()); client.register(text(email), text(password)); show("注册请求已提交；如后台开启邮箱验证，请完成验证后登录。"); });
    }

    private void receivePushes() { execute(this::receivePushesInternal); }

    private void receivePushesInternal() throws Exception {
        JsonArray pushes = client.memberPushes();
        int sourceCount = 0, liveCount = 0; String updateName = "", updateUrl = "", message = "";
        for (JsonElement value : pushes) {
            if (!value.isJsonObject()) continue;
            JsonObject push = value.getAsJsonObject();
            if (message.isEmpty()) message = string(push, "message");
            JsonArray sources = array(push, "configurationSources");
            for (JsonElement sourceValue : sources) {
                if (!sourceValue.isJsonObject()) continue;
                String address = string(sourceValue.getAsJsonObject(), "address");
                if (address.isEmpty()) continue;
                if (sourceCount++ == 0) Hawk.put(HawkConfig.API_URL, address);
            }
            JsonArray lives = array(push, "liveSources");
            for (JsonElement liveValue : lives) {
                if (!liveValue.isJsonObject()) continue;
                String address = string(liveValue.getAsJsonObject(), "address");
                if (address.isEmpty()) continue;
                if (liveCount++ == 0) Hawk.put(HawkConfig.LIVE_API_URL, address);
            }
            String version = string(push, "androidVersion"), url = string(push, "androidDownloadUrl");
            if (!version.isEmpty() && !url.isEmpty() && newer(version, "1.3.2")) { updateName = version; updateUrl = url; }
        }
        final int configs = sourceCount, lives = liveCount; final String note = message, latest = updateName, download = updateUrl;
        runOnUiThread(() -> {
            if (configs > 0) ApiConfig.get().loadConfig(false, new ApiConfig.LoadConfigCallback() {
                @Override public void success() { status.setText(summary(configs, lives, note, latest)); }
                @Override public void error(String value) { status.setText(summary(configs, lives, note, latest) + "\n配置刷新失败：" + value); }
                @Override public void notice(String value) { status.setText(summary(configs, lives, note, latest) + "\n" + value); }
            }, MembershipActivity.this);
            else status.setText(summary(configs, lives, note, latest));
            if (!download.isEmpty()) new android.app.AlertDialog.Builder(MembershipActivity.this)
                    .setTitle("发现 MT播放器 Android " + latest).setMessage(note.isEmpty() ? "后台已推送新版本。" : note)
                    .setPositiveButton("单击下载", (d, w) -> startActivity(new Intent(Intent.ACTION_VIEW, Uri.parse(download))))
                    .setNegativeButton("稍后", null).show();
        });
    }

    private String summary(int configs, int lives, String note, String latest) {
        String result = "已接收会员推送：配置源 " + configs + " 个，直播源 " + lives + " 个。";
        if (!note.isEmpty()) result += "\n" + note;
        if (!latest.isEmpty()) result += "\n发现 Android 新版本：" + latest;
        return result;
    }

    private void execute(Task task) { status.setText("正在请求服务器…"); new Thread(() -> { try { task.run(); } catch (Exception e) { show("操作失败：" + e.getMessage()); } }).start(); }
    private void show(String value) { runOnUiThread(() -> status.setText(value)); }
    private static String text(EditText value) { return value.getText().toString().trim(); }
    private static JsonArray array(JsonObject value, String name) { return value.has(name) && value.get(name).isJsonArray() ? value.getAsJsonArray(name) : new JsonArray(); }
    private static String string(JsonObject value, String name) { return value.has(name) && !value.get(name).isJsonNull() ? value.get(name).getAsString() : ""; }
    private static boolean newer(String value, String current) { String[] a = value.split("\\."), b = current.split("\\."); for (int i = 0; i < Math.max(a.length, b.length); i++) { int x = i < a.length ? number(a[i]) : 0, y = i < b.length ? number(b[i]) : 0; if (x != y) return x > y; } return false; }
    private static int number(String value) { try { return Integer.parseInt(value.replaceAll("[^0-9]", "")); } catch (Exception ignored) { return 0; } }
    private interface Task { void run() throws Exception; }
}
