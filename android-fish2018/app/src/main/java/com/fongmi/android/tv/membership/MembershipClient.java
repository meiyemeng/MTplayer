package com.fongmi.android.tv.membership;

import android.content.Context;
import android.content.SharedPreferences;
import android.util.Base64;

import com.fongmi.android.tv.App;
import com.google.gson.JsonArray;
import com.google.gson.JsonObject;

import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.util.UUID;

import okhttp3.MediaType;
import okhttp3.OkHttpClient;
import okhttp3.Request;
import okhttp3.RequestBody;
import okhttp3.Response;

public final class MembershipClient {

    private static final String DEFAULT_SERVER = "https://mtplayer.salego.cn";
    private static final MediaType JSON = MediaType.get("application/json; charset=utf-8");
    private final SharedPreferences prefs;
    private final OkHttpClient client;

    public MembershipClient(Context context) {
        prefs = context.getSharedPreferences("mtplayer.membership", Context.MODE_PRIVATE);
        client = new OkHttpClient.Builder().build();
    }

    public String getServer() {
        return prefs.getString("server", DEFAULT_SERVER);
    }

    public String getEmail() {
        return prefs.getString("email", "");
    }

    public boolean isSignedIn() {
        return !prefs.getString("access", "").isEmpty();
    }

    public void setServer(String value) {
        String server = value == null ? "" : value.trim();
        if (server.isEmpty()) server = DEFAULT_SERVER;
        if (!server.matches("^https://[^/]+(?:/.*)?$")) throw new IllegalArgumentException("同步服务器必须使用 HTTPS 地址");
        while (server.endsWith("/")) server = server.substring(0, server.length() - 1);
        prefs.edit().putString("server", server).apply();
    }

    public void register(String email, String password) throws IOException {
        JsonObject request = new JsonObject();
        request.addProperty("email", email);
        request.addProperty("password", password);
        exchange("/api/v1/auth/register", request, false, false);
    }

    public void login(String email, String password) throws IOException {
        JsonObject request = new JsonObject();
        request.addProperty("email", email);
        request.addProperty("password", password);
        request.addProperty("deviceName", "MT播放器 Android");
        request.addProperty("platform", "android-tvbox");
        saveTokens(parseObject(exchange("/api/v1/auth/login", request, false, true)), email);
    }

    public void logout() {
        prefs.edit().remove("access").remove("refresh").remove("email").remove("deviceId").remove("backupVersion").apply();
    }

    public JsonArray resources() throws IOException {
        return parseArray(authorized("/api/v1/member/resources", null));
    }

    public JsonArray updates() throws IOException {
        return parseArray(authorized("/api/v1/member/updates/android", null));
    }

    public void uploadBackup(String backup) throws IOException {
        JsonObject payload = new JsonObject();
        payload.addProperty("key", "fongmiBackup");
        payload.addProperty("value", backup);
        JsonObject mutation = new JsonObject();
        mutation.addProperty("id", backupId());
        mutation.addProperty("kind", "Preference");
        mutation.addProperty("baseVersion", prefs.getLong("backupVersion", 0));
        mutation.addProperty("modifiedAtUtc", java.time.OffsetDateTime.now(java.time.ZoneOffset.UTC).toString());
        mutation.addProperty("isDeleted", false);
        mutation.add("payload", payload);
        JsonArray mutations = new JsonArray();
        mutations.add(mutation);
        JsonObject request = new JsonObject();
        request.addProperty("deviceId", deviceId());
        request.add("mutations", mutations);
        JsonArray result = parseArray(authorized("/api/v1/sync/push", request));
        if (result.size() == 0 || !result.get(0).getAsJsonObject().get("accepted").getAsBoolean()) {
            throw new IOException("云端未接受本次上传，请退出账号后重新登录");
        }
        prefs.edit().putLong("backupVersion", result.get(0).getAsJsonObject().get("version").getAsLong()).apply();
    }

    public String downloadBackup() throws IOException {
        long cursor = 0;
        String backup = null;
        while (true) {
            JsonObject page = parseObject(authorized("/api/v1/sync/pull?cursor=" + cursor + "&limit=500", null));
            JsonArray changes = page.has("changes") ? page.getAsJsonArray("changes") : new JsonArray();
            for (int i = 0; i < changes.size(); i++) {
                JsonObject change = changes.get(i).getAsJsonObject();
                if (!backupId().equals(string(change, "id")) || booleanValue(change, "isDeleted")) continue;
                JsonObject payload = change.getAsJsonObject("payload");
                if (payload != null && "fongmiBackup".equals(string(payload, "key"))) backup = string(payload, "value");
                prefs.edit().putLong("backupVersion", longValue(change, "baseVersion")).apply();
            }
            long next = longValue(page, "cursor");
            if (changes.size() < 500 || next == cursor) break;
            cursor = next;
        }
        if (backup == null || backup.isEmpty()) throw new IOException("云端还没有可下载的配置，请先在一台设备上上传");
        return backup;
    }

    private String authorized(String path, JsonObject body) throws IOException {
        ResponseData response = execute(path, body, prefs.getString("access", ""));
        if (response.status == 401 && refresh()) response = execute(path, body, prefs.getString("access", ""));
        if (response.status < 200 || response.status >= 300) throw failure(response);
        return response.body;
    }

    private ResponseData execute(String path, JsonObject body, String token) throws IOException {
        Request.Builder builder = new Request.Builder().url(getServer() + path).header("Authorization", "Bearer " + token);
        if (body == null) builder.get(); else builder.post(RequestBody.create(JSON, App.gson().toJson(body)));
        try (Response response = client.newCall(builder.build()).execute()) {
            return new ResponseData(response.code(), response.body() == null ? "" : response.body().string());
        }
    }

    private boolean refresh() {
        String token = prefs.getString("refresh", "");
        if (token.isEmpty()) return false;
        JsonObject request = new JsonObject();
        request.addProperty("refreshToken", token);
        try {
            saveTokens(parseObject(exchange("/api/v1/auth/refresh", request, false, true)), getEmail());
            return true;
        } catch (Exception e) {
            logout();
            return false;
        }
    }

    private String exchange(String path, JsonObject body, boolean authorized, boolean expectBody) throws IOException {
        Request.Builder builder = new Request.Builder().url(getServer() + path);
        if (authorized) builder.header("Authorization", "Bearer " + prefs.getString("access", ""));
        builder.post(RequestBody.create(JSON, App.gson().toJson(body)));
        try (Response response = client.newCall(builder.build()).execute()) {
            String value = response.body() == null ? "" : response.body().string();
            if (!response.isSuccessful()) throw failure(new ResponseData(response.code(), value));
            return expectBody ? value : "{}";
        }
    }

    private void saveTokens(JsonObject response, String email) throws IOException {
        String access = string(response, "accessToken");
        String refresh = string(response, "refreshToken");
        if (access.isEmpty() || refresh.isEmpty()) throw new IOException("服务器未返回登录令牌");
        String session = sessionId(access);
        prefs.edit().putString("access", access).putString("refresh", refresh).putString("email", email)
                .putString("deviceId", session).apply();
    }

    private String deviceId() throws IOException {
        String value = prefs.getString("deviceId", "");
        if (value.isEmpty()) throw new IOException("登录会话无效，请退出后重新登录");
        return value;
    }

    private String backupId() {
        return UUID.nameUUIDFromBytes("mtplayer:fongmi:backup".getBytes(StandardCharsets.UTF_8)).toString();
    }

    private static String sessionId(String token) {
        try {
            String[] parts = token.split("\\.");
            byte[] data = Base64.decode(parts[1], Base64.URL_SAFE | Base64.NO_WRAP | Base64.NO_PADDING);
            return UUID.fromString(string(App.gson().fromJson(new String(data, StandardCharsets.UTF_8), JsonObject.class), "sid")).toString();
        } catch (Exception e) {
            return "";
        }
    }

    private static IOException failure(ResponseData response) {
        String detail = "";
        try {
            JsonObject problem = parseObject(response.body);
            detail = string(problem, "code");
            if (detail.isEmpty()) detail = string(problem, "title");
        } catch (Exception ignored) {
        }
        return new IOException("服务器请求失败（HTTP " + response.status + (detail.isEmpty() ? "" : "，" + detail) + "）");
    }

    private static JsonObject parseObject(String value) {
        JsonObject result = App.gson().fromJson(value, JsonObject.class);
        return result == null ? new JsonObject() : result;
    }

    private static JsonArray parseArray(String value) {
        JsonArray result = App.gson().fromJson(value, JsonArray.class);
        return result == null ? new JsonArray() : result;
    }

    private static String string(JsonObject value, String name) {
        return value != null && value.has(name) && !value.get(name).isJsonNull() ? value.get(name).getAsString() : "";
    }

    private static long longValue(JsonObject value, String name) {
        try { return value.has(name) ? value.get(name).getAsLong() : 0; } catch (Exception e) { return 0; }
    }

    private static boolean booleanValue(JsonObject value, String name) {
        try { return value.has(name) && value.get(name).getAsBoolean(); } catch (Exception e) { return false; }
    }

    private record ResponseData(int status, String body) {}
}
