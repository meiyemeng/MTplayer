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
import java.net.URLDecoder;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.Collections;
import java.util.LinkedHashMap;
import java.util.Iterator;
import java.util.List;
import java.util.Map;
import java.util.UUID;

import cn.mtplayer.core.model.Site;
import cn.mtplayer.core.model.LiveChannel;
import okhttp3.OkHttpClient;
import okhttp3.Request;
import okhttp3.Response;

public final class ConfigurationRepository {
    private static final int MAX_CONFIG_BYTES = 10 * 1024 * 1024;
    private static final Type GROUP_LIST = new TypeToken<List<SourceGroup>>(){}.getType();
    private static final Type STRING_LIST = new TypeToken<List<String>>(){}.getType();
    private static final Type LIVE_LIST = new TypeToken<List<LiveChannel>>(){}.getType();
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

    public synchronized void replaceManagedGroups(List<SourceGroup> managed) {
        List<String> oldIds = gson.fromJson(prefs.getString("managed.groups", "[]"), STRING_LIST);
        if (oldIds == null) oldIds = new ArrayList<>();
        List<SourceGroup> values = groups();
        final List<String> removeIds = oldIds;
        values.removeIf(group -> removeIds.contains(group.id));
        values.addAll(managed);
        save(values);
        List<String> ids = new ArrayList<>(); for (SourceGroup group : managed) ids.add(group.id);
        prefs.edit().putString("managed.groups", gson.toJson(ids)).apply();
    }

    public synchronized void replaceManagedLives(List<LiveChannel> channels) {
        prefs.edit().putString("managed.lives", gson.toJson(channels)).apply();
    }

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
                // A successfully parsed configuration is authoritative even when
                // empty. Otherwise switching sources leaves stale posters behind.
                prefs.edit().putString("cache." + group.id, gson.toJson(fresh)).apply();
                cached = fresh;
            } catch (IOException | RuntimeException ex) {
                if (cached.isEmpty()) throw ex;
            }
            for (Site site : cached) merged.put(site.key, site);
        }
        return new ArrayList<>(merged.values());
    }

    public List<LiveChannel> enabledLiveChannels() throws IOException {
        Map<String, LiveChannel> merged = new LinkedHashMap<>();
        List<LiveChannel> managed = gson.fromJson(prefs.getString("managed.lives", "[]"), LIVE_LIST);
        if (managed != null) for (LiveChannel channel : managed) if (channel.url != null) merged.put(channel.url, channel);
        IOException lastFailure = null;
        for (SourceGroup group : groups()) {
            if (!group.enabled) continue;
            List<LiveChannel> channels = readLiveCache(group.id);
            try {
                List<LiveChannel> fresh = loadLiveChannels(group.url, group.name, 0);
                prefs.edit().putString("livecache." + group.id, gson.toJson(fresh)).apply();
                channels = fresh;
            } catch (IOException exception) {
                lastFailure = exception;
            }
            for (LiveChannel channel : channels) {
                if (channel.url != null && !channel.url.isEmpty()) merged.put(channel.url, channel);
            }
        }
        if (merged.isEmpty() && lastFailure != null) throw lastFailure;
        return new ArrayList<>(merged.values());
    }

    private List<LiveChannel> loadLiveChannels(String configUrl, String fallbackGroup, int depth) throws IOException {
        if (depth > 3) return Collections.emptyList();
        JsonElement root = parsePossiblyWrapped(download(configUrl));
        if (!root.isJsonObject()) return Collections.emptyList();
        JsonObject object = root.getAsJsonObject();
        List<LiveChannel> result = new ArrayList<>();
        JsonArray lives = array(object, "lives");
        if (lives != null) {
            for (JsonElement item : lives) {
                String name = fallbackGroup;
                String address = null;
                if (item.isJsonPrimitive()) address = item.getAsString();
                else if (item.isJsonObject()) {
                    JsonObject live = item.getAsJsonObject();
                    name = first(text(live, "name"), fallbackGroup, "直播");
                    address = first(text(live, "url"), text(live, "api"), text(live, "address"));
                    JsonArray nestedChannels = array(live, "channels");
                    if (nestedChannels != null) {
                        for (JsonElement nestedElement : nestedChannels) {
                            if (!nestedElement.isJsonObject()) continue;
                            JsonObject nested = nestedElement.getAsJsonObject();
                            String nestedName = first(text(nested, "name"), name, "直播");
                            JsonArray nestedUrls = array(nested, "urls");
                            if (nestedUrls == null) continue;
                            for (JsonElement nestedUrl : nestedUrls) {
                                String value = nestedUrl.isJsonPrimitive() ? nestedUrl.getAsString()
                                        : nestedUrl.isJsonObject() ? first(text(nestedUrl.getAsJsonObject(), "url"), text(nestedUrl.getAsJsonObject(), "api")) : null;
                                addLiveAddress(result, nestedName, resolve(configUrl, value), depth);
                            }
                        }
                    }
                }
                addLiveAddress(result, name, resolve(configUrl, address), depth);
            }
        }
        JsonArray channels = array(object, "channels");
        if (channels != null) {
            for (JsonElement channelElement : channels) {
                if (!channelElement.isJsonObject()) continue;
                JsonObject channel = channelElement.getAsJsonObject();
                String name = first(text(channel, "name"), fallbackGroup, "直播");
                JsonArray urls = array(channel, "urls");
                if (urls == null) continue;
                for (JsonElement urlElement : urls) {
                    String address = urlElement.isJsonPrimitive() ? urlElement.getAsString()
                            : urlElement.isJsonObject() ? first(text(urlElement.getAsJsonObject(), "url"), text(urlElement.getAsJsonObject(), "api")) : null;
                    addLiveAddress(result, name, resolve(configUrl, address), depth);
                }
            }
        }
        JsonArray urls = array(object, "urls");
        if (urls != null) {
            for (JsonElement item : urls) {
                String child = item.isJsonPrimitive() ? item.getAsString()
                        : item.isJsonObject() ? first(text(item.getAsJsonObject(), "url"), text(item.getAsJsonObject(), "api")) : null;
                if (child != null && !child.trim().isEmpty()) result.addAll(loadLiveChannels(resolve(configUrl, child.trim()), fallbackGroup, depth + 1));
            }
        }
        return result;
    }

    private void addLiveAddress(List<LiveChannel> result, String group, String rawAddress, int depth) {
        String address = decodeProxyAddress(rawAddress);
        if (address == null || !(address.startsWith("http://") || address.startsWith("https://"))) return;
        try {
            if (address.toLowerCase().contains(".m3u8") && !address.toLowerCase().endsWith(".m3u") && !address.toLowerCase().endsWith(".txt")) {
                result.add(new LiveChannel(group, group, address, ""));
                return;
            }
            String playlist = download(address);
            if (playlist.toUpperCase().contains("#EXTM3U")) result.addAll(parseM3u(group, playlist));
            else result.addAll(parseTxt(group, playlist));
        } catch (IOException | RuntimeException ignored) { }
    }

    private static List<LiveChannel> parseM3u(String fallbackGroup, String text) {
        List<LiveChannel> result = new ArrayList<>();
        String pendingName = null, pendingGroup = fallbackGroup, pendingLogo = "";
        for (String raw : text.replace("\r", "").split("\n")) {
            String line = raw.trim();
            if (line.startsWith("#EXTINF")) {
                int comma = line.indexOf(',');
                pendingName = comma >= 0 ? line.substring(comma + 1).trim() : "直播频道";
                pendingGroup = attribute(line, "group-title", fallbackGroup);
                pendingLogo = attribute(line, "tvg-logo", "");
            } else if (!line.isEmpty() && !line.startsWith("#") && (line.startsWith("http://") || line.startsWith("https://"))) {
                result.add(new LiveChannel(pendingGroup, first(pendingName, "直播频道"), line, pendingLogo));
                pendingName = null; pendingGroup = fallbackGroup; pendingLogo = "";
            }
        }
        return result;
    }

    private static List<LiveChannel> parseTxt(String fallbackGroup, String text) {
        List<LiveChannel> result = new ArrayList<>();
        String group = fallbackGroup;
        for (String raw : text.replace("\r", "").split("\n")) {
            String line = raw.trim();
            if (line.isEmpty() || line.startsWith("#")) continue;
            String[] parts = line.split(",", 2);
            if (parts.length < 2) continue;
            if ("#genre#".equalsIgnoreCase(parts[1].trim())) { group = parts[0].trim(); continue; }
            String address = parts[1].trim();
            if (address.startsWith("http://") || address.startsWith("https://")) result.add(new LiveChannel(group, parts[0].trim(), address, ""));
        }
        return result;
    }

    private static String attribute(String line, String name, String fallback) {
        String token = name + "=\"";
        int start = line.indexOf(token);
        if (start < 0) return fallback;
        start += token.length();
        int end = line.indexOf('"', start);
        return end > start ? line.substring(start, end) : fallback;
    }

    private static String decodeProxyAddress(String raw) {
        if (raw == null) return null;
        String value = raw.trim();
        if (!value.startsWith("proxy://")) return value;
        int index = value.indexOf("ext=");
        if (index < 0) return null;
        String ext = value.substring(index + 4);
        try { ext = URLDecoder.decode(ext, StandardCharsets.UTF_8.name()); } catch (Exception ignored) { }
        if (ext.startsWith("http://") || ext.startsWith("https://")) return ext;
        try { return new String(Base64.decode(ext, Base64.URL_SAFE | Base64.NO_WRAP), StandardCharsets.UTF_8); }
        catch (IllegalArgumentException exception) {
            try { return new String(Base64.decode(ext, Base64.DEFAULT), StandardCharsets.UTF_8); }
            catch (IllegalArgumentException ignored) { return null; }
        }
    }

    private List<Site> loadSites(String url, String groupId, int depth) throws IOException {
        if (depth > 3) return Collections.emptyList();
        JsonElement root = parsePossiblyWrapped(download(url));
        if (!root.isJsonObject()) return Collections.emptyList();
        JsonObject obj = root.getAsJsonObject();
        List<Site> result = new ArrayList<>();
        String profileJar = resolveDecorated(url, first(text(obj, "spider"), text(obj, "jar")));
        JsonArray sites = array(obj, "sites");
        if (sites != null) {
            int index = 0;
            for (JsonElement element : sites) {
                if (!element.isJsonObject()) continue;
                JsonObject value = element.getAsJsonObject();
                String api = text(value, "api");
                int type = number(value, "type", 0);
                boolean direct = api != null && (api.startsWith("https://") || api.startsWith("http://")) &&
                        (type == 0 || type == 1 || type == 2 || type == 4);
                boolean csp = type == 3 && api != null && api.startsWith("csp_");
                if (!direct && !csp) continue;
                index++;
                String name = first(text(value, "name"), text(value, "key"), "接口 " + index);
                String key = first(text(value, "key"), "site" + index);
                String jar = resolveDecorated(url, first(text(value, "jar"), profileJar));
                JsonElement rawExt = value.get("ext");
                String ext = rawExt == null || rawExt.isJsonNull() ? "" :
                        rawExt.isJsonPrimitive() ? rawExt.getAsString() : gson.toJson(rawExt);
                boolean searchable = number(value, "searchable", 1) != 0;
                result.add(new Site(groupId + ":" + key, name, api, type == 0 ? 1 : type, jar, ext, searchable));
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
                result.addAll(loadSites(resolve(url, childUrl.trim()), groupId + ":g" + (++index), depth + 1));
            }
        }
        return result;
    }

    private String download(String url) throws IOException {
        Request request = new Request.Builder().url(url).header("User-Agent", "MTPlayer/1.3.2 Android").build();
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
            element = TvBoxConfigurationPayloadDecoder.parseLenient(value);
        } catch (RuntimeException exception) {
            throw new IllegalArgumentException("配置接口返回的内容不是有效 JSON", exception);
        }
        if (element != null && element.isJsonPrimitive() && element.getAsJsonPrimitive().isString()) {
            value = element.getAsString();
            try { value = new String(Base64.decode(value, Base64.DEFAULT), StandardCharsets.UTF_8); } catch (IllegalArgumentException ignored) { }
            element = TvBoxConfigurationPayloadDecoder.parseLenient(value);
        }
        if (element != null && element.isJsonObject()) {
            JsonObject object = element.getAsJsonObject();
            for (String key : new String[]{"data", "config", "payload"}) {
                JsonElement wrapped = object.get(key);
                if (wrapped != null && wrapped.isJsonPrimitive() && wrapped.getAsJsonPrimitive().isString()) {
                    String raw = wrapped.getAsString();
                    try { raw = new String(Base64.decode(raw, Base64.DEFAULT), StandardCharsets.UTF_8); } catch (IllegalArgumentException ignored) { }
                    try { JsonElement parsed = TvBoxConfigurationPayloadDecoder.parseLenient(raw); if (parsed != null && parsed.isJsonObject()) return parsed; } catch (RuntimeException ignored) { }
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

    private List<LiveChannel> readLiveCache(String id) {
        String json = prefs.getString("livecache." + id, "[]");
        Type type = new TypeToken<List<LiveChannel>>(){}.getType();
        List<LiveChannel> channels = gson.fromJson(json, type);
        return channels == null ? new ArrayList<>() : channels;
    }

    private void save(List<SourceGroup> groups) { prefs.edit().putString("groups", gson.toJson(groups)).apply(); }
    private static JsonArray array(JsonObject obj, String key) { JsonElement e = obj.get(key); return e != null && e.isJsonArray() ? e.getAsJsonArray() : null; }
    private static String text(JsonObject obj, String key) { JsonElement e = obj.get(key); return e != null && !e.isJsonNull() && e.isJsonPrimitive() ? e.getAsString() : null; }
    private static int number(JsonObject obj, String key, int fallback) { JsonElement e = obj.get(key); try { return e != null && e.isJsonPrimitive() ? e.getAsInt() : fallback; } catch (RuntimeException ex) { return fallback; } }
    private static String first(String... values) { for (String value : values) if (value != null && !value.trim().isEmpty()) return value; return null; }
    private static String resolve(String parent, String child) { try { return URI.create(parent).resolve(child).toString(); } catch (RuntimeException ex) { return child; } }
    private static String resolveDecorated(String parent, String child) {
        if (child == null || child.trim().isEmpty()) return child;
        String value = child.trim();
        int marker = value.indexOf(";md5;");
        String suffix = marker >= 0 ? value.substring(marker) : "";
        String address = marker >= 0 ? value.substring(0, marker) : value;
        return resolve(parent, address) + suffix;
    }
    private static String requireHttpUrl(String value) {
        String url = value == null ? "" : value.trim();
        URI uri;
        try { uri = URI.create(url); } catch (RuntimeException ex) { throw new IllegalArgumentException("请输入有效的 HTTP 或 HTTPS 配置地址"); }
        boolean supported = "https".equalsIgnoreCase(uri.getScheme()) || "http".equalsIgnoreCase(uri.getScheme());
        if (!supported || uri.getHost() == null) throw new IllegalArgumentException("配置地址必须以 http:// 或 https:// 开头");
        return uri.toString();
    }
}
