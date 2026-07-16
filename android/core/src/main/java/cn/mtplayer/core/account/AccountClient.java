package cn.mtplayer.core.account;

import android.content.Context;
import android.content.SharedPreferences;

import com.google.gson.Gson;
import com.google.gson.JsonElement;
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
    public String email() { return prefs.getString("email", ""); }
    public boolean signedIn() { return !prefs.getString("refresh", "").isEmpty(); }

    public void bind(String value) {
        String url = value == null ? "" : value.trim();
        URI uri;
        try { uri = URI.create(url); }
        catch (RuntimeException ex) { throw new IllegalArgumentException("请输入有效的 HTTPS 同步服务器地址"); }
        if (!"https".equalsIgnoreCase(uri.getScheme()) || uri.getHost() == null ||
                (uri.getPath() != null && !uri.getPath().isEmpty() && !"/".equals(uri.getPath()))) {
            throw new IllegalArgumentException("同步服务器必须使用 HTTPS 域名，不要填写 API 路径");
        }
        while (url.endsWith("/")) url = url.substring(0, url.length() - 1);
        prefs.edit().putString("server", url).apply();
    }

    public void register(String email, String password) throws IOException {
        JsonObject body = new JsonObject();
        body.addProperty("email", email);
        body.addProperty("password", password);
        post("/api/v1/auth/register", body, false);
    }

    public void login(String email, String password, String platform) throws IOException {
        JsonObject body = new JsonObject();
        body.addProperty("email", email);
        body.addProperty("password", password);
        body.addProperty("deviceName", "MT播放器 Android");
        body.addProperty("platform", platform);
        saveTokens(post("/api/v1/auth/login", body, true), email);
    }

    public void logout() {
        prefs.edit().remove("refresh").remove("access").remove("email").apply();
    }

    public JsonElement authorizedGet(String path) throws IOException {
        return authorized(path, null, false);
    }

    public JsonElement authorizedPost(String path, JsonObject body) throws IOException {
        return authorized(path, body, true);
    }

    public TvCode beginTvLogin(String serverName) throws IOException {
        String origin = requireServer();
        okhttp3.HttpUrl url = okhttp3.HttpUrl.parse(origin + "/api/v1/auth/tv/device-code");
        if (url == null) throw new IOException("同步服务器地址无效");
        Request request = new Request.Builder().url(url.newBuilder().addQueryParameter("serverName", serverName).build()).get().build();
        try (Response response = http.newCall(request).execute()) {
            String raw = response.body() == null ? "" : response.body().string();
            if (!response.isSuccessful()) throw problem(response.code(), raw, "获取电视登录码失败");
            TvCode code = gson.fromJson(raw, TvCode.class);
            if (code == null || code.deviceCode == null) throw new IOException("服务器未返回设备码");
            return code;
        }
    }

    public boolean pollTvLogin(String deviceCode) throws IOException {
        JsonObject body = new JsonObject(); body.addProperty("deviceCode", deviceCode);
        Request request = new Request.Builder().url(requireServer() + "/api/v1/auth/tv/token")
                .post(RequestBody.create(gson.toJson(body), JSON)).build();
        try (Response response = http.newCall(request).execute()) {
            String raw = response.body() == null ? "" : response.body().string();
            if (response.code() == 428 || response.code() == 429) return false;
            if (!response.isSuccessful()) throw problem(response.code(), raw, "设备码已失效，请重新获取");
            saveTokens(gson.fromJson(raw, JsonObject.class), "");
            return true;
        }
    }

    private JsonElement authorized(String path, JsonObject body, boolean post) throws IOException {
        if (!signedIn()) throw new IOException("请先登录账户");
        ResponseData first = sendAuthorized(path, body, post, prefs.getString("access", ""));
        if (first.code == 401) {
            refresh();
            first = sendAuthorized(path, body, post, prefs.getString("access", ""));
        }
        if (first.code < 200 || first.code >= 300) throw problem(first.code, first.body, "同步请求失败");
        JsonElement parsed = gson.fromJson(first.body, JsonElement.class);
        return parsed == null ? new JsonObject() : parsed;
    }

    private ResponseData sendAuthorized(String path, JsonObject body, boolean post, String access) throws IOException {
        Request.Builder builder = new Request.Builder().url(requireServer() + path)
                .header("Authorization", "Bearer " + access);
        Request request = post ? builder.post(RequestBody.create(gson.toJson(body), JSON)).build() : builder.get().build();
        try (Response response = http.newCall(request).execute()) {
            return new ResponseData(response.code(), response.body() == null ? "" : response.body().string());
        }
    }

    private void refresh() throws IOException {
        String token = prefs.getString("refresh", "");
        if (token.isEmpty()) throw new IOException("登录已失效，请重新登录");
        JsonObject body = new JsonObject(); body.addProperty("refreshToken", token);
        saveTokens(post("/api/v1/auth/refresh", body, true), email());
    }

    private JsonObject post(String path, JsonObject value, boolean requireJson) throws IOException {
        Request request = new Request.Builder().url(requireServer() + path)
                .post(RequestBody.create(gson.toJson(value), JSON)).build();
        try (Response response = http.newCall(request).execute()) {
            String raw = response.body() == null ? "" : response.body().string();
            if (!response.isSuccessful()) throw problem(response.code(), raw, "服务器请求失败");
            if (!requireJson || raw.trim().isEmpty()) return new JsonObject();
            JsonObject parsed = gson.fromJson(raw, JsonObject.class);
            return parsed == null ? new JsonObject() : parsed;
        }
    }

    private void saveTokens(JsonObject response, String email) throws IOException {
        String refresh = string(response, "refreshToken");
        String access = string(response, "accessToken");
        if (refresh.isEmpty() || access.isEmpty()) throw new IOException("服务器未返回完整登录令牌");
        SharedPreferences.Editor editor = prefs.edit().putString("refresh", refresh).putString("access", access);
        if (email != null && !email.isEmpty()) editor.putString("email", email);
        editor.apply();
    }

    private String requireServer() throws IOException {
        String origin = serverUrl();
        if (origin.isEmpty()) throw new IOException("请先填写并保存同步服务器地址");
        return origin;
    }

    private IOException problem(int code, String raw, String fallback) {
        try {
            JsonObject value = gson.fromJson(raw, JsonObject.class);
            if (value != null) {
                String title = string(value, "title");
                if (!title.isEmpty()) return new IOException(title);
                String problemCode = string(value, "code");
                if (!problemCode.isEmpty()) return new IOException(problemCode + "（HTTP " + code + "）");
            }
        } catch (RuntimeException ignored) { }
        return new IOException(fallback + "（HTTP " + code + "）");
    }

    private static String string(JsonObject value, String name) {
        return value != null && value.has(name) && !value.get(name).isJsonNull() ? value.get(name).getAsString() : "";
    }

    private static final class ResponseData {
        final int code; final String body;
        ResponseData(int code, String body) { this.code = code; this.body = body; }
    }
}
