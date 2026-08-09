package com.github.tvbox.osc.membership;

import android.content.Context;
import android.content.SharedPreferences;
import android.util.Base64;

import com.google.gson.Gson;
import com.google.gson.JsonArray;
import com.google.gson.JsonObject;

import java.io.IOException;
import java.net.URI;
import java.nio.charset.StandardCharsets;
import java.util.UUID;

import okhttp3.MediaType;
import okhttp3.OkHttpClient;
import okhttp3.Request;
import okhttp3.RequestBody;
import okhttp3.Response;

/** Client for account login, cloud data sync, resource distribution and app update checks. */
public final class MembershipClient {
    private static final String DEFAULT_SERVER = "https://mtplayer.salego.cn";
    private static final MediaType JSON = MediaType.get("application/json; charset=utf-8");
    private final SharedPreferences prefs;
    private final OkHttpClient http = new OkHttpClient.Builder().build();
    private final Gson gson = new Gson();

    public MembershipClient(Context context) {
        prefs = context.getSharedPreferences("mtplayer.membership", Context.MODE_PRIVATE);
    }

    public String server() { return prefs.getString("server", DEFAULT_SERVER); }
    public String email() { return prefs.getString("email", ""); }
    public boolean signedIn() { return !prefs.getString("access", "").isEmpty(); }

    /** The sync API identifies a device by the authenticated session id (JWT sid). */
    public String deviceId() {
        String session = sessionId(prefs.getString("access", ""));
        if (!session.isEmpty()) {
            prefs.edit().putString("deviceId", session).apply();
            return session;
        }
        String value = prefs.getString("deviceId", "");
        if (!value.isEmpty()) return value;
        value = UUID.randomUUID().toString();
        prefs.edit().putString("deviceId", value).apply();
        return value;
    }

    public long cursor() { return prefs.getLong("syncCursor", 0L); }
    public void saveCursor(long value) { prefs.edit().putLong("syncCursor", Math.max(0L, value)).apply(); }
    public long version(String key) { return prefs.getLong("syncVersion." + key, 0L); }
    public void saveVersion(String key, long value) { prefs.edit().putLong("syncVersion." + key, Math.max(0L, value)).apply(); }

    public void saveServer(String value) {
        String url = value == null ? "" : value.trim();
        if (url.isEmpty()) url = DEFAULT_SERVER;
        URI uri = URI.create(url);
        if (!"https".equalsIgnoreCase(uri.getScheme()) || uri.getHost() == null) {
            throw new IllegalArgumentException("同步服务器必须是 HTTPS 域名");
        }
        while (url.endsWith("/")) url = url.substring(0, url.length() - 1);
        prefs.edit().putString("server", url).apply();
    }

    public void register(String email, String password) throws IOException {
        JsonObject request = new JsonObject();
        request.addProperty("email", email);
        request.addProperty("password", password);
        post("/api/v1/auth/register", request, false);
    }

    public void login(String email, String password) throws IOException {
        JsonObject request = new JsonObject();
        request.addProperty("email", email);
        request.addProperty("password", password);
        request.addProperty("deviceName", "MT播放器 Android TV");
        request.addProperty("platform", "android-tvbox");
        saveTokens(post("/api/v1/auth/login", request, true), email);
    }

    public void logout() {
        prefs.edit().remove("access").remove("refresh").remove("email")
            .remove("deviceId").remove("syncCursor").apply();
    }

    /** Member-distributed point-on-demand and live source URLs. */
    public JsonArray memberResources() throws IOException { return getArray("/api/v1/member/resources"); }

    /** Android version notices and download URLs, intentionally separate from resource distribution. */
    public JsonArray androidUpdates() throws IOException { return getArray("/api/v1/member/updates/android"); }

    public JsonArray syncPush(JsonArray mutations) throws IOException {
        JsonObject request = new JsonObject();
        request.addProperty("deviceId", deviceId());
        request.add("mutations", mutations);
        return postAuthorizedArray("/api/v1/sync/push", request);
    }

    public JsonObject syncPull(long cursor) throws IOException {
        return getObject("/api/v1/sync/pull?cursor=" + Math.max(0, cursor) + "&limit=500");
    }

    private JsonArray getArray(String path) throws IOException {
        String body = authorizedExchange(path, null);
        JsonArray result = gson.fromJson(body, JsonArray.class);
        return result == null ? new JsonArray() : result;
    }

    private JsonObject getObject(String path) throws IOException {
        String body = authorizedExchange(path, null);
        JsonObject result = gson.fromJson(body, JsonObject.class);
        return result == null ? new JsonObject() : result;
    }

    private JsonArray postAuthorizedArray(String path, JsonObject value) throws IOException {
        String body = authorizedExchange(path, value);
        JsonArray result = gson.fromJson(body, JsonArray.class);
        return result == null ? new JsonArray() : result;
    }

    /** Execute once, refresh an expired access token on 401, then retry exactly once. */
    private String authorizedExchange(String path, JsonObject value) throws IOException {
        if (!signedIn()) throw new IOException("请先登录账户");
        ResponseData response = executeAuthorized(path, value);
        if (response.status == 401 && refreshTokens()) response = executeAuthorized(path, value);
        if (response.status < 200 || response.status >= 300) throw failure(response.status, response.body);
        return response.body;
    }

    private ResponseData executeAuthorized(String path, JsonObject value) throws IOException {
        Request.Builder builder = new Request.Builder().url(requireServer() + path)
            .header("Authorization", "Bearer " + prefs.getString("access", ""));
        if (value == null) builder.get();
        else builder.post(RequestBody.create(JSON, gson.toJson(value)));
        try (Response response = http.newCall(builder.build()).execute()) {
            return new ResponseData(response.code(), response.body() == null ? "" : response.body().string());
        }
    }

    private boolean refreshTokens() {
        String refresh = prefs.getString("refresh", "");
        if (refresh.isEmpty()) return false;
        JsonObject request = new JsonObject();
        request.addProperty("refreshToken", refresh);
        try {
            saveTokens(post("/api/v1/auth/refresh", request, true), email());
            return true;
        } catch (Exception ignored) {
            prefs.edit().remove("access").remove("refresh").remove("deviceId").apply();
            return false;
        }
    }

    private void saveTokens(JsonObject result, String accountEmail) throws IOException {
        String access = string(result, "accessToken");
        String refresh = string(result, "refreshToken");
        if (access.isEmpty() || refresh.isEmpty()) throw new IOException("服务器未返回登录令牌");
        String session = sessionId(access);
        SharedPreferences.Editor editor = prefs.edit().putString("access", access)
            .putString("refresh", refresh).putString("email", accountEmail == null ? "" : accountEmail);
        if (!session.isEmpty()) editor.putString("deviceId", session);
        editor.apply();
    }

    private JsonObject post(String path, JsonObject value, boolean expectBody) throws IOException {
        Request request = new Request.Builder().url(requireServer() + path)
            .post(RequestBody.create(JSON, gson.toJson(value))).build();
        try (Response response = http.newCall(request).execute()) {
            String body = response.body() == null ? "" : response.body().string();
            if (!response.isSuccessful()) throw failure(response.code(), body);
            if (!expectBody || body.trim().isEmpty()) return new JsonObject();
            JsonObject result = gson.fromJson(body, JsonObject.class);
            return result == null ? new JsonObject() : result;
        }
    }

    private IOException failure(int status, String body) {
        String detail = "";
        try {
            JsonObject problem = gson.fromJson(body, JsonObject.class);
            String code = string(problem, "code");
            String title = string(problem, "title");
            detail = !code.isEmpty() ? code : title;
        } catch (Exception ignored) { }
        return new IOException("服务器请求失败（HTTP " + status + (detail.isEmpty() ? "" : "，" + detail) + "）");
    }

    private String requireServer() throws IOException {
        String value = server();
        if (value.isEmpty()) throw new IOException("请先填写同步服务器地址");
        return value;
    }

    static String sessionId(String token) {
        try {
            String[] parts = token.split("\\.");
            if (parts.length < 2) return "";
            byte[] decoded = Base64.decode(parts[1], Base64.URL_SAFE | Base64.NO_WRAP | Base64.NO_PADDING);
            JsonObject payload = new Gson().fromJson(new String(decoded, StandardCharsets.UTF_8), JsonObject.class);
            return UUID.fromString(string(payload, "sid")).toString();
        } catch (Exception ignored) { return ""; }
    }

    private static String string(JsonObject value, String name) {
        return value != null && value.has(name) && !value.get(name).isJsonNull() ? value.get(name).getAsString() : "";
    }

    private static final class ResponseData {
        final int status;
        final String body;
        ResponseData(int status, String body) { this.status = status; this.body = body; }
    }
}
