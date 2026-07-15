package cn.mtplayer.core.account;

import android.content.Context;
import android.content.SharedPreferences;

import com.google.gson.Gson;
import com.google.gson.JsonObject;

import java.io.IOException;
import java.net.URI;

import okhttp3.MediaType;
import okhttp3.OkHttpClient;
import okhttp3.Request;
import okhttp3.RequestBody;
import okhttp3.Response;

public final class AccountClient {
    public static final class TvCode {
        public String deviceCode;
        public String userCode;
        public String verificationUri;
        public String expiresAtUtc;
        public int pollIntervalSeconds;
    }
    private static final MediaType JSON = MediaType.get("application/json; charset=utf-8");
    private final SharedPreferences prefs;
    private final OkHttpClient http;
    private final Gson gson = new Gson();

    public AccountClient(Context context, OkHttpClient http) {
        prefs = context.getSharedPreferences("mtplayer.account", Context.MODE_PRIVATE);
        this.http = http;
    }

    public String serverUrl() { return prefs.getString("server", ""); }
    public boolean signedIn() { return !prefs.getString("refresh", "").isEmpty(); }
    public void bind(String value) {
        String url = value == null ? "" : value.trim();
        URI uri;
        try { uri = URI.create(url); } catch (RuntimeException ex) { throw new IllegalArgumentException("请输入有效的 HTTPS 服务器地址"); }
        if (!"https".equalsIgnoreCase(uri.getScheme()) || uri.getHost() == null || (uri.getPath() != null && !uri.getPath().isEmpty() && !"/".equals(uri.getPath())))
            throw new IllegalArgumentException("服务器地址必须是 HTTPS 域名，不要填写端口后的路径");
        while (url.endsWith("/")) url = url.substring(0, url.length() - 1);
        prefs.edit().putString("server", url).apply();
    }

    public void register(String email, String password) throws IOException {
        JsonObject body = new JsonObject(); body.addProperty("email", email); body.addProperty("password", password);
        post("/api/v1/auth/register", body, false);
    }

    public void login(String email, String password, String platform) throws IOException {
        JsonObject body = new JsonObject(); body.addProperty("email", email); body.addProperty("password", password);
        body.addProperty("deviceName", "MT播放器 Android"); body.addProperty("platform", platform);
        JsonObject response = post("/api/v1/auth/login", body, true);
        String refresh = response.has("refreshToken") ? response.get("refreshToken").getAsString() : "";
        String access = response.has("accessToken") ? response.get("accessToken").getAsString() : "";
        if (refresh.isEmpty()) throw new IOException("服务器未返回登录令牌");
        prefs.edit().putString("refresh", refresh).putString("access", access).putString("email", email).apply();
    }

    public void logout() { prefs.edit().remove("refresh").remove("access").remove("email").apply(); }

    public TvCode beginTvLogin(String serverName) throws IOException {
        String origin = serverUrl(); if (origin.isEmpty()) throw new IOException("请先绑定服务器地址");
        okhttp3.HttpUrl url = okhttp3.HttpUrl.parse(origin + "/api/v1/auth/tv/device-code");
        if (url == null) throw new IOException("服务器地址无效");
        Request request = new Request.Builder().url(url.newBuilder().addQueryParameter("serverName", serverName).build()).get().build();
        try (Response response = http.newCall(request).execute()) {
            String raw = response.body() == null ? "" : response.body().string();
            if (!response.isSuccessful()) throw new IOException("设备登录请求失败：HTTP " + response.code());
            TvCode code = gson.fromJson(raw, TvCode.class);
            if (code == null || code.deviceCode == null) throw new IOException("服务器未返回设备码");
            return code;
        }
    }

    public boolean pollTvLogin(String deviceCode) throws IOException {
        JsonObject body = new JsonObject(); body.addProperty("deviceCode", deviceCode);
        String origin = serverUrl(); if (origin.isEmpty()) throw new IOException("请先绑定服务器地址");
        Request request = new Request.Builder().url(origin + "/api/v1/auth/tv/token").post(RequestBody.create(gson.toJson(body), JSON)).build();
        try (Response response = http.newCall(request).execute()) {
            String raw = response.body() == null ? "" : response.body().string();
            if (response.code() == 428 || response.code() == 429) return false;
            if (!response.isSuccessful()) throw new IOException("设备码已失效，请重新获取");
            JsonObject tokens = gson.fromJson(raw, JsonObject.class);
            String refresh = tokens != null && tokens.has("refreshToken") ? tokens.get("refreshToken").getAsString() : "";
            String access = tokens != null && tokens.has("accessToken") ? tokens.get("accessToken").getAsString() : "";
            if (refresh.isEmpty()) throw new IOException("服务器未返回登录令牌");
            prefs.edit().putString("refresh", refresh).putString("access", access).apply();
            return true;
        }
    }

    private JsonObject post(String path, JsonObject value, boolean requireJson) throws IOException {
        String origin = serverUrl(); if (origin.isEmpty()) throw new IOException("请先绑定服务器地址");
        Request request = new Request.Builder().url(origin + path).post(RequestBody.create(gson.toJson(value), JSON)).build();
        try (Response response = http.newCall(request).execute()) {
            String raw = response.body() == null ? "" : response.body().string();
            if (!response.isSuccessful()) {
                try { JsonObject problem = gson.fromJson(raw, JsonObject.class); if (problem != null && problem.has("title")) throw new IOException(problem.get("title").getAsString()); } catch (RuntimeException ignored) { }
                throw new IOException("服务器返回 HTTP " + response.code());
            }
            if (!requireJson || raw.isBlank()) return new JsonObject();
            JsonObject parsed = gson.fromJson(raw, JsonObject.class);
            return parsed == null ? new JsonObject() : parsed;
        }
    }
}
