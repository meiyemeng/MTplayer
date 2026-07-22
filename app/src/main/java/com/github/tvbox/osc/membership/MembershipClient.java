package com.github.tvbox.osc.membership;

import android.content.Context;
import android.content.SharedPreferences;

import com.google.gson.Gson;
import com.google.gson.JsonArray;
import com.google.gson.JsonObject;

import java.io.IOException;
import java.net.URI;

import okhttp3.MediaType;
import okhttp3.OkHttpClient;
import okhttp3.Request;
import okhttp3.RequestBody;
import okhttp3.Response;

/** Minimal client for the MT Player membership server. Media playback never passes through this API. */
public final class MembershipClient {
    private static final MediaType JSON = MediaType.get("application/json; charset=utf-8");
    private final SharedPreferences prefs;
    private final OkHttpClient http = new OkHttpClient.Builder().build();
    private final Gson gson = new Gson();

    public MembershipClient(Context context) {
        prefs = context.getSharedPreferences("mtplayer.membership", Context.MODE_PRIVATE);
    }

    public String server() { return prefs.getString("server", ""); }
    public boolean signedIn() { return !prefs.getString("access", "").isEmpty(); }

    public void saveServer(String value) {
        String url = value == null ? "" : value.trim();
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
        request.addProperty("deviceName", "MT播放器 Android");
        request.addProperty("platform", "android-tvbox");
        JsonObject result = post("/api/v1/auth/login", request, true);
        String access = string(result, "accessToken");
        String refresh = string(result, "refreshToken");
        if (access.isEmpty() || refresh.isEmpty()) throw new IOException("服务器未返回登录令牌");
        prefs.edit().putString("access", access).putString("refresh", refresh).putString("email", email).apply();
    }

    public void logout() { prefs.edit().remove("access").remove("refresh").remove("email").apply(); }

    public JsonArray memberPushes() throws IOException {
        if (!signedIn()) throw new IOException("请先登录账户");
        Request request = new Request.Builder().url(requireServer() + "/api/v1/member/pushes")
                .header("Authorization", "Bearer " + prefs.getString("access", "")).get().build();
        try (Response response = http.newCall(request).execute()) {
            String body = response.body() == null ? "" : response.body().string();
            if (!response.isSuccessful()) throw new IOException("会员推送请求失败（HTTP " + response.code() + "）");
            return gson.fromJson(body, JsonArray.class);
        }
    }

    private JsonObject post(String path, JsonObject value, boolean expectBody) throws IOException {
        Request request = new Request.Builder().url(requireServer() + path)
                .post(RequestBody.create(JSON, gson.toJson(value))).build();
        try (Response response = http.newCall(request).execute()) {
            String body = response.body() == null ? "" : response.body().string();
            if (!response.isSuccessful()) throw new IOException("服务器请求失败（HTTP " + response.code() + "）");
            if (!expectBody || body.trim().isEmpty()) return new JsonObject();
            JsonObject result = gson.fromJson(body, JsonObject.class);
            return result == null ? new JsonObject() : result;
        }
    }

    private String requireServer() throws IOException {
        if (server().isEmpty()) throw new IOException("请先填写同步服务器地址");
        return server();
    }

    private static String string(JsonObject value, String name) {
        return value != null && value.has(name) && !value.get(name).isJsonNull() ? value.get(name).getAsString() : "";
    }
}
