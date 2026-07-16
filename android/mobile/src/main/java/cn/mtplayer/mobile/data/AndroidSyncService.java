package cn.mtplayer.mobile.data;

import android.content.Context;
import android.content.SharedPreferences;

import com.google.gson.JsonArray;
import com.google.gson.JsonElement;
import com.google.gson.JsonObject;

import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.time.Instant;
import java.util.UUID;

import cn.mtplayer.core.account.AccountClient;
import cn.mtplayer.core.config.ConfigurationRepository;
import cn.mtplayer.core.config.SourceGroup;
import cn.mtplayer.core.model.MediaItem;

public final class AndroidSyncService {
    public static final class Result {
        public final int pushed;
        public final int pulled;
        Result(int pushed, int pulled) { this.pushed = pushed; this.pulled = pulled; }
    }

    private final AccountClient account;
    private final ConfigurationRepository configurations;
    private final LocalLibrary library;
    private final SharedPreferences prefs;

    public AndroidSyncService(Context context, AccountClient account, ConfigurationRepository configurations, LocalLibrary library) {
        this.account = account;
        this.configurations = configurations;
        this.library = library;
        this.prefs = context.getSharedPreferences("mtplayer.sync", Context.MODE_PRIVATE);
    }

    public Result synchronize() throws Exception {
        if (!account.signedIn()) throw new IllegalStateException("请先登录后再同步");
        JsonArray mutations = new JsonArray();
        for (MediaItem item : library.favorites()) {
            if (blank(item.siteKey) || blank(item.id)) continue;
            JsonObject payload = mediaPayload(item);
            mutations.add(mutation(stableId("favorite", item.siteKey, item.id), "Favorite", payload));
        }
        for (String key : library.favoriteTombstones()) {
            String[] parts = key.split("\\n", 2);
            if (parts.length == 2) mutations.add(tombstone(stableId("favorite", parts[0], parts[1]), "Favorite"));
        }
        for (MediaItem item : library.history()) {
            if (blank(item.siteKey) || blank(item.id)) continue;
            JsonObject payload = mediaPayload(item);
            payload.addProperty("interfaceKey", item.siteKey);
            payload.addProperty("lineName", "default");
            payload.addProperty("episodeIndex", 0);
            payload.addProperty("positionMs", 0);
            payload.addProperty("durationMs", 0);
            mutations.add(mutation(stableId("playback", item.siteKey, item.id), "PlaybackHistory", payload));
        }
        for (SourceGroup group : configurations.groups()) {
            JsonObject payload = new JsonObject();
            payload.addProperty("name", fallback(group.name, "配置源"));
            payload.addProperty("address", group.url);
            payload.addProperty("isEnabled", group.enabled);
            mutations.add(mutation(group.id, "ConfigurationGroup", payload));
        }
        for (String id : configurations.deletedIds()) mutations.add(tombstone(id, "ConfigurationGroup"));

        JsonObject request = new JsonObject();
        request.addProperty("deviceId", deviceId());
        request.add("mutations", mutations);
        JsonElement pushedRaw = account.authorizedPost("/api/v1/sync/push", request);
        int pushed = 0;
        if (pushedRaw.isJsonArray()) {
            for (JsonElement value : pushedRaw.getAsJsonArray()) {
                if (!value.isJsonObject()) continue;
                JsonObject result = value.getAsJsonObject();
                String id = string(result, "id");
                if (!id.isEmpty() && result.has("version")) prefs.edit().putLong("version." + id, result.get("version").getAsLong()).apply();
                if (bool(result, "accepted")) pushed++;
                JsonElement current = result.get("current");
                if (current != null && current.isJsonObject()) apply(current.getAsJsonObject());
            }
            library.clearFavoriteTombstones();
            configurations.clearDeletedIds();
        }

        long cursor = prefs.getLong("cursor", 0);
        JsonElement pulledRaw = account.authorizedGet("/api/v1/sync/pull?cursor=" + cursor + "&limit=500");
        int pulled = 0;
        if (pulledRaw.isJsonObject()) {
            JsonObject response = pulledRaw.getAsJsonObject();
            JsonElement changes = response.get("changes");
            if (changes != null && changes.isJsonArray()) {
                for (JsonElement value : changes.getAsJsonArray()) {
                    if (value.isJsonObject()) { apply(value.getAsJsonObject()); pulled++; }
                }
            }
            if (response.has("cursor")) prefs.edit().putLong("cursor", response.get("cursor").getAsLong()).apply();
        }
        return new Result(pushed, pulled);
    }

    private JsonObject mutation(String id, String kind, JsonObject payload) {
        JsonObject value = new JsonObject();
        value.addProperty("id", id);
        value.addProperty("kind", kind);
        value.addProperty("baseVersion", prefs.getLong("version." + id, 0));
        value.addProperty("modifiedAtUtc", Instant.now().toString());
        value.addProperty("isDeleted", false);
        value.add("payload", payload);
        return value;
    }

    private JsonObject tombstone(String id, String kind) {
        JsonObject value = mutation(id, kind, new JsonObject());
        value.addProperty("isDeleted", true);
        return value;
    }

    private void apply(JsonObject mutation) {
        String id = string(mutation, "id");
        String kind = string(mutation, "kind");
        boolean deleted = bool(mutation, "isDeleted");
        JsonObject payload = mutation.has("payload") && mutation.get("payload").isJsonObject()
                ? mutation.getAsJsonObject("payload") : new JsonObject();
        if (!id.isEmpty() && mutation.has("baseVersion")) prefs.edit().putLong("version." + id, mutation.get("baseVersion").getAsLong()).apply();
        if ("Favorite".equals(kind)) {
            MediaItem item = deleted ? findBySyncId(library.favorites(), "favorite", id) : readMedia(payload);
            if (item != null) library.applySyncedFavorite(item, deleted);
        }
        else if ("PlaybackHistory".equals(kind)) {
            MediaItem item = deleted ? findBySyncId(library.history(), "playback", id) : readMedia(payload);
            if (item != null) library.applySyncedHistory(item, deleted);
        }
        else if ("ConfigurationGroup".equals(kind)) {
            String address = string(payload, "address");
            if (deleted || !address.isEmpty()) configurations.applySynced(id, string(payload, "name"), address, bool(payload, "isEnabled"), deleted);
        }
    }

    private static JsonObject mediaPayload(MediaItem item) {
        JsonObject value = new JsonObject();
        value.addProperty("sourceKey", item.siteKey);
        value.addProperty("contentId", item.id);
        value.addProperty("title", fallback(item.name, "未命名影片"));
        value.addProperty("category", fallback(item.type, ""));
        value.addProperty("caption", fallback(item.remarks, ""));
        value.addProperty("coverUrl", fallback(item.poster, ""));
        return value;
    }

    private static MediaItem readMedia(JsonObject value) {
        MediaItem item = new MediaItem();
        item.siteKey = string(value, "sourceKey");
        item.siteName = string(value, "interfaceKey");
        item.id = string(value, "contentId");
        item.name = string(value, "title");
        item.poster = string(value, "coverUrl");
        item.remarks = string(value, "caption");
        item.type = string(value, "category");
        return item;
    }

    private static MediaItem findBySyncId(Iterable<MediaItem> values, String kind, String id) {
        try { for (MediaItem item : values) if (stableId(kind, item.siteKey, item.id).equalsIgnoreCase(id)) return item; }
        catch (Exception ignored) { }
        return null;
    }

    private String deviceId() {
        String id = prefs.getString("deviceId", "");
        if (!id.isEmpty()) return id;
        id = UUID.randomUUID().toString();
        prefs.edit().putString("deviceId", id).apply();
        return id;
    }

    private static String stableId(String kind, String... values) throws Exception {
        String joined = kind + "\n" + String.join("\n", values);
        byte[] hash = MessageDigest.getInstance("SHA-256").digest(joined.getBytes(StandardCharsets.UTF_8));
        byte[] bytes = new byte[16]; System.arraycopy(hash, 0, bytes, 0, 16);
        bytes[7] = (byte)((bytes[7] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        ByteBuffer b = ByteBuffer.wrap(bytes).order(ByteOrder.LITTLE_ENDIAN);
        long first = ((long)b.getInt() & 0xffffffffL) << 32;
        first |= ((long)b.getShort() & 0xffffL) << 16;
        first |= (long)b.getShort() & 0xffffL;
        long second = ByteBuffer.wrap(bytes, 8, 8).getLong();
        return new UUID(first, second).toString();
    }

    private static String string(JsonObject value, String name) { return value.has(name) && !value.get(name).isJsonNull() ? value.get(name).getAsString() : ""; }
    private static boolean bool(JsonObject value, String name) { return value.has(name) && value.get(name).isJsonPrimitive() && value.get(name).getAsBoolean(); }
    private static boolean blank(String value) { return value == null || value.trim().isEmpty(); }
    private static String fallback(String value, String fallback) { return blank(value) ? fallback : value; }
}
