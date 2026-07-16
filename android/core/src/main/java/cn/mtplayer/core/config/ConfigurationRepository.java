package cn.mtplayer.core.config;

import android.content.Context;
import android.content.SharedPreferences;
import android.util.Base64;

import com.google.gson.Gson;
import com.google.gson.JsonArray;
import com.google.gson.JsonElement;
import com.google.gson.JsonObject;
import com.google.gson.reflect.TypeToken;

import java.io.IOException;
import java.lang.reflect.Type;
import java.net.URI;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.Collections;
import java.util.LinkedHashMap;
import java.util.Iterator;
import java.util.List;
import java.util.Map;
import java.util.UUID;

import cn.mtplayer.core.model.Site;
import okhttp3.OkHttpClient;
import okhttp3.Request;
import okhttp3.Response;

public final class ConfigurationRepository {
    private static final int MAX_CONFIG_BYTES = 10 * 1024 * 1024;
    private static final Type GROUP_LIST = new TypeToken<List<SourceGroup>>(){}.getType();
    private static final Type STRING_LIST = new TypeToken<List<String>>(){}.getType();
    private final SharedPreferences prefs;
    private final OkHttpClient http;
    private final Gson gson = new Gson();

    public ConfigurationRepository(Context context, OkHttpClient http) {
        this.prefs = context.getSharedPreferences("mtplayer.sources", Context.MODE_PRIVATE);
        this.http = http;
    }

    public synchronized List<SourceGroup> groups() {
        String json = prefs.getString("groups", "[]");
        List<SourceGroup> groups = gson.fromJson(json, GROUP_LIST);
        return groups == null ? new ArrayList<>() : new ArrayList<>(groups);
    }

    public synchronized SourceGroup add(String name, String url) {
        String normalized = requireHttpUrl(url);
        List<SourceGroup> groups = groups();
        for (SourceGroup group : groups) {
            if (group.url.equalsIgnoreCase(normalized)) {
                group.name = name == null || name.trim().isEmpty() ? group.name : name.trim();
                group.enabled = true;
                save(groups);
                return group;
            }
        }
        SourceGroup group = new SourceGroup(UUID.randomUUID().toString(),
                name == null || name.trim().isEmpty() ? "配置源 " + (groups.size() + 1) : name.trim(),
                normalized, true);
        groups.add(group);
        save(groups);
        return group;
    }

    public synchronized void setEnabled(String id, boolean enabled) {
        List<SourceGroup> groups = groups();
        for (SourceGroup group : groups) if (group.id.equals(id)) group.enabled = enabled;
        save(groups);
    }

    public synchronized void remove(String id) {
        List<SourceGroup> groups = groups();
        for (Iterator<SourceGroup> iterator = groups.iterator(); iterator.hasNext();) if (iterator.next().id.equals(id)) iterator.remove();
        save(groups);
        List<String> deleted = deletedIds();
        if (!deleted.contains(id)) deleted.add(id);
        prefs.edit().putString("deleted", gson.toJson(deleted)).apply();
    }

    public synchronized List<String> deletedIds() {
        List<String> values = gson.fromJson(prefs.getString("deleted", "[]"), STRING_LIST);
        return values == null ? new ArrayList<>() : new ArrayList<>(values);
    }

    public synchronized void clearDeletedIds() { prefs.edit().remove("deleted").apply(); }

    public synchronized void applySynced(String id, String name, String url, boolean enabled, boolean deleted) {
        List<SourceGroup> groups = groups();
        for (Iterator<SourceGroup> iterator = groups.iterator(); iterator.hasNext();) if (iterator.next().id.equals(id)) iterator.remove();
        if (!deleted) groups.add(new SourceGroup(id, name, requireHttpUrl(url), enabled));
        save(groups);
    }

    public List<Site> enabledSites() throws IOException {
        Map<String, Site> merged = new LinkedHashMap<>();
        for (SourceGroup group : groups()) {
            if (!group.enabled) continue;
            List<Site> cached = readCache(group.id);
            try {
                List<Site> fresh = loadSites(group.url, group.id, 0);
                if (!fresh.isEmpty()) {
                    prefs.edit().putString("cache." + group.id, gson.toJson(fresh)).apply();
                    cached = fresh;
                }
            } catch (IOException | RuntimeException ex) {
                if (cached.isEmpty()) throw ex;
            }
            for (Site site : cached) merged.put(site.key, site);
        }
        return new ArrayList<>(merged.values());
    }

    private List<Site> loadSites(String url, String groupId, int depth) throws IOException {
        if (depth > 3) return Collections.emptyList();
        String body = download(url);
        JsonElement root = parsePossiblyWrapped(body);
        if (!root.isJsonObject()) return Collections.emptyList();
        JsonObject obj = root.getAsJsonObject();
        List<Site> result = new ArrayList<>();
        JsonArray sites = array(obj, "sites");
        if (sites != null) {
            int index = 0;
            for (JsonElement element : sites) {
                if (!element.isJsonObject()) continue;
                JsonObject siteObject = element.getAsJsonObject();
                String api = text(siteObject, "api");
                if (api == null || !(api.startsWith("https://") || api.startsWith("http://"))) continue;
                int type = number(siteObject, "type", 0);
                boolean cms = type == 1 || api.contains("/provide/vod") || api.contains("/api.php/provide/");
                if (!cms) continue;
                String name = first(text(siteObject, "name"), text(siteObject, "key"), "接口 " + (++index));
                String key = first(text(siteObject, "key"), "site" + index);
                result.add(new Site(groupId + ":" + key, name, api));
            }
        }
        JsonArray urls = array(obj, "urls");
        if (urls != null) {
            int index = 0;
            for (JsonElement element : urls) {
                String childUrl = null;
                if (element.isJsonPrimitive()) childUrl = element.getAsString();
                else if (element.isJsonObject()) childUrl = first(text(element.getAsJsonObject(), "url"), text(element.getAsJsonObject(), "api"));
                if (childUrl == null || childUrl.trim().isEmpty()) continue;
                childUrl = resolve(url, childUrl.trim());
                result.addAll(loadSites(childUrl, groupId + ":g" + (++index), depth + 1));
            }
        }
        return result;
    }

    private String download(String url) throws IOException {
        Request request = new Request.Builder().url(url).header("User-Agent", "MTPlayer/1.1 Android").build();
        try (Response response = http.newCall(request).execute()) {
            if (!response.isSuccessful() || response.body() == null) throw new IOException("配置下载失败：HTTP " + response.code());
            if (response.body().contentLength() > MAX_CONFIG_BYTES) throw new IOException("配置文件超过 10 MiB");
            String body = response.body().string();
            if (body.getBytes(StandardCharsets.UTF_8).length > MAX_CONFIG_BYTES) throw new IOException("配置文件超过 10 MiB");
            return body;
        }
    }

    private JsonElement parsePossiblyWrapped(String body) {
        String value = TvBoxConfigurationPayloadDecoder.decode(body);
        JsonElement element;
        try {
            element = gson.fromJson(value, JsonElement.class);
        } catch (RuntimeException exception) {
            throw new IllegalArgumentException("配置接口返回的内容不是有效 JSON", exception);
        }
        if (element != null && element.isJsonPrimitive() && element.getAsJsonPrimitive().isString()) {
            value = element.getAsString();
            try { value = new String(Base64.decode(value, Base64.DEFAULT), StandardCharsets.UTF_8); } catch (IllegalArgumentException ignored) { }
            element = gson.fromJson(value, JsonElement.class);
        }
        if (element != null && element.isJsonObject()) {
            JsonObject object = element.getAsJsonObject();
            for (String key : new String[]{"data", "config", "payload"}) {
                JsonElement wrapped = object.get(key);
                if (wrapped != null && wrapped.isJsonPrimitive() && wrapped.getAsJsonPrimitive().isString()) {
                    String raw = wrapped.getAsString();
                    try { raw = new String(Base64.decode(raw, Base64.DEFAULT), StandardCharsets.UTF_8); } catch (IllegalArgumentException ignored) { }
                    try { JsonElement parsed = gson.fromJson(raw, JsonElement.class); if (parsed != null && parsed.isJsonObject()) return parsed; } catch (RuntimeException ignored) { }
                }
            }
        }
        return element;
    }

    private List<Site> readCache(String id) {
        String json = prefs.getString("cache." + id, "[]");
        Type type = new TypeToken<List<Site>>(){}.getType();
        List<Site> sites = gson.fromJson(json, type);
        return sites == null ? new ArrayList<>() : sites;
    }

    private void save(List<SourceGroup> groups) { prefs.edit().putString("groups", gson.toJson(groups)).apply(); }
    private static JsonArray array(JsonObject obj, String key) { JsonElement e = obj.get(key); return e != null && e.isJsonArray() ? e.getAsJsonArray() : null; }
    private static String text(JsonObject obj, String key) { JsonElement e = obj.get(key); return e != null && !e.isJsonNull() && e.isJsonPrimitive() ? e.getAsString() : null; }
    private static int number(JsonObject obj, String key, int fallback) { JsonElement e = obj.get(key); try { return e != null && e.isJsonPrimitive() ? e.getAsInt() : fallback; } catch (RuntimeException ex) { return fallback; } }
    private static String first(String... values) { for (String value : values) if (value != null && !value.trim().isEmpty()) return value; return null; }
    private static String resolve(String parent, String child) { try { return URI.create(parent).resolve(child).toString(); } catch (RuntimeException ex) { return child; } }
    private static String requireHttpUrl(String value) {
        String url = value == null ? "" : value.trim();
        URI uri;
        try { uri = URI.create(url); } catch (RuntimeException ex) { throw new IllegalArgumentException("请输入有效的 HTTP 或 HTTPS 配置地址"); }
        boolean supported = "https".equalsIgnoreCase(uri.getScheme()) || "http".equalsIgnoreCase(uri.getScheme());
        if (!supported || uri.getHost() == null) throw new IllegalArgumentException("配置地址必须以 http:// 或 https:// 开头");
        return uri.toString();
    }
}
