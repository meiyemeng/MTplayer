package com.fongmi.android.tv.ui.dialog;

import android.app.Dialog;
import android.view.LayoutInflater;
import android.view.Window;
import android.view.WindowManager;

import androidx.fragment.app.FragmentActivity;

import com.fongmi.android.tv.App;
import com.fongmi.android.tv.R;
import com.fongmi.android.tv.bean.Backup;
import com.fongmi.android.tv.bean.Config;
import com.fongmi.android.tv.databinding.DialogMembershipBinding;
import com.fongmi.android.tv.membership.MembershipClient;
import com.fongmi.android.tv.utils.Notify;
import com.fongmi.android.tv.utils.ResUtil;
import com.fongmi.android.tv.utils.Task;
import com.google.gson.JsonArray;
import com.google.gson.JsonElement;
import com.google.gson.JsonObject;

import java.nio.charset.StandardCharsets;
import java.util.concurrent.atomic.AtomicBoolean;

public final class MembershipDialog {

    private final FragmentActivity activity;
    private final DialogMembershipBinding binding;
    private final MembershipClient client;
    private final AtomicBoolean busy = new AtomicBoolean();
    private Dialog dialog;

    private MembershipDialog(FragmentActivity activity) {
        this.activity = activity;
        this.binding = DialogMembershipBinding.inflate(LayoutInflater.from(activity));
        this.client = new MembershipClient(activity);
    }

    public static void show(FragmentActivity activity) {
        new MembershipDialog(activity).show();
    }

    private void show() {
        binding.server.setText(client.getServer());
        refreshAccount();
        dialog = LightDialog.create(activity, null, binding.getRoot());
        binding.login.setOnClickListener(v -> login());
        binding.register.setOnClickListener(v -> register());
        binding.upload.setOnClickListener(v -> upload());
        binding.download.setOnClickListener(v -> download());
        binding.resources.setOnClickListener(v -> receiveResources());
        binding.logout.setOnClickListener(v -> {
            client.logout();
            binding.password.setText("");
            refreshAccount();
            binding.summary.setText(R.string.member_guest);
        });
        binding.close.setOnClickListener(v -> dialog.dismiss());
        dialog.setCanceledOnTouchOutside(false);
        dialog.show();
        Window window = dialog.getWindow();
        if (window != null) {
            int width = (int) (ResUtil.getScreenWidth(activity) * (ResUtil.isLand(activity) ? 0.64f : 0.94f));
            window.setLayout(width, WindowManager.LayoutParams.WRAP_CONTENT);
        }
        if (client.isSignedIn()) binding.upload.requestFocus(); else binding.server.requestFocus();
    }

    private void refreshAccount() {
        boolean signed = client.isSignedIn();
        binding.authForm.setVisibility(signed ? android.view.View.GONE : android.view.View.VISIBLE);
        binding.signedPanel.setVisibility(signed ? android.view.View.VISIBLE : android.view.View.GONE);
        if (signed) {
            binding.account.setText("已登录：" + client.getEmail() + "\n" + client.getServer());
            binding.summary.setText("云端仅保存你主动上传的配置；影视播放流不经过服务器。");
        }
    }

    private void login() {
        run("正在登录…", () -> {
            client.setServer(text(binding.server));
            client.login(text(binding.email), text(binding.password));
            App.post(() -> {
                refreshAccount();
                binding.upload.requestFocus();
            });
        }, "登录成功");
    }

    private void register() {
        run("正在注册…", () -> {
            client.setServer(text(binding.server));
            client.register(text(binding.email), text(binding.password));
        }, "注册请求已提交；如服务器启用了邮箱验证，请先查收验证邮件");
    }

    private void upload() {
        run("正在上传完整配置…", () -> {
            String backup = Backup.create().toString();
            if (backup.getBytes(StandardCharsets.UTF_8).length > 1900 * 1024) {
                throw new IllegalStateException("本机配置超过云端单次上传限制，请先清理过多的播放历史");
            }
            client.uploadBackup(backup);
        }, "本机配置已上传到云端");
    }

    private void download() {
        run("正在下载云端配置…", () -> Backup.objectFrom(client.downloadBackup()).restore(), "云端配置已恢复");
    }

    private void receiveResources() {
        run("正在接收会员资源…", () -> {
            JsonArray pushes = client.resources();
            int vod = 0;
            int live = 0;
            for (JsonElement value : pushes) {
                if (!value.isJsonObject()) continue;
                JsonObject push = value.getAsJsonObject();
                JsonArray configs = array(push, "configurationSources");
                for (JsonElement item : configs) {
                    if (!item.isJsonObject()) continue;
                    String address = string(item.getAsJsonObject(), "address");
                    if (!address.isEmpty()) {
                        Config.find(address, string(item.getAsJsonObject(), "name"), 0);
                        vod++;
                    }
                }
                JsonArray lives = array(push, "liveSources");
                for (JsonElement item : lives) {
                    if (!item.isJsonObject()) continue;
                    String address = string(item.getAsJsonObject(), "address");
                    if (!address.isEmpty()) {
                        Config.find(address, string(item.getAsJsonObject(), "name"), 1);
                        live++;
                    }
                }
            }
            if (vod + live == 0) throw new IllegalStateException("后台当前没有适用于此账号的资源推送");
            final int vodCount = vod;
            final int liveCount = live;
            App.post(() -> binding.summary.setText("已接收点播 " + vodCount + " 个、直播 " + liveCount + " 个，可在原版配置历史中自由切换。"));
        }, "会员资源已保存");
    }

    private void run(String progress, CheckedRunnable action, String success) {
        if (!busy.compareAndSet(false, true)) return;
        binding.summary.setText(progress);
        Task.execute(() -> {
            try {
                action.run();
                App.post(() -> {
                    binding.summary.setText(success);
                    busy.set(false);
                });
            } catch (Throwable e) {
                App.post(() -> {
                    binding.summary.setText("操作失败：" + (e.getMessage() == null ? e.getClass().getSimpleName() : e.getMessage()));
                    Notify.show(binding.summary.getText().toString());
                    busy.set(false);
                });
            }
        });
    }

    private static String text(android.widget.EditText editText) {
        return editText.getText() == null ? "" : editText.getText().toString().trim();
    }

    private static JsonArray array(JsonObject value, String name) {
        return value.has(name) && value.get(name).isJsonArray() ? value.getAsJsonArray(name) : new JsonArray();
    }

    private static String string(JsonObject value, String name) {
        return value.has(name) && !value.get(name).isJsonNull() ? value.get(name).getAsString() : "";
    }

    private interface CheckedRunnable {
        void run() throws Exception;
    }
}
