package cn.mtplayer.core.catalogue;

import android.content.Context;

import com.google.gson.Gson;
import com.google.gson.JsonArray;
import com.google.gson.JsonElement;
import com.google.gson.JsonObject;

import java.io.IOException;
import java.util.ArrayList;
import java.util.Collections;
import java.util.LinkedHashMap;
import java.util.Iterator;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.concurrent.Callable;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.Future;
import java.util.concurrent.TimeUnit;

import cn.mtplayer.core.model.Episode;
import cn.mtplayer.core.model.MediaDetail;
import cn.mtplayer.core.model.MediaItem;
import cn.mtplayer.core.model.PlayLine;
import cn.mtplayer.core.model.Site;
import cn.mtplayer.core.spider.CspSpiderRuntime;
import okhttp3.HttpUrl;
import okhttp3.OkHttpClient;
import okhttp3.Request;
import okhttp3.Response;

public final class CmsCatalogueClient {
    public static final class ResolvedPlayback {
        public final String url;
        public final boolean requiresParser;
        public ResolvedPlayback(String url, boolean requiresParser) {
            this.url = url;
            this.requiresParser = requiresParser;
        }
    }
    private final OkHttpClient http;
    private final Gson gson = new Gson();
    private final CspSpiderRuntime csp;

    public CmsCatalogueClient(Context context, OkHttpClient http) {
        this.http = http;
        this.csp = new CspSpiderRuntime(context, http);
    }

    /** Retained for JVM unit tests which exercise direct HTTP CMS providers only. */
    public CmsCatalogueClient(OkHttpClient http) { this.http = http; this.csp = null; }

    public List<MediaItem> searchAll(List<Site> sites, String keyword) {
        if (sites.isEmpty() || keyword == null || keyword.trim().isEmpty()) return Collections.emptyList();
        ExecutorService pool = Executors.newFixedThreadPool(Math.min(8, sites.size()));
        List<Future<List<MediaItem>>> futures = new ArrayList<>();
        for (Site site : sites) if (site.searchable) futures.add(pool.submit(() -> query(site, keyword.trim(), false)));
        Map<String, MediaItem> merged = new LinkedHashMap<>();
        for (Future<List<MediaItem>> future : futures) {
            try {
                for (MediaItem item : future.get(13, TimeUnit.SECONDS)) {
                    String key = normalize(item.name) + "|" + (item.year == null ? "" : item.year);
                    merged.putIfAbsent(key + "|" + item.siteKey, item);
                }
            } catch (Exception ignored) { future.cancel(true); }
        }
        pool.shutdownNow();
        return new ArrayList<>(merged.values());
    }

    public List<MediaItem> latest(List<Site> sites, int limit) {
        if (sites.isEmpty()) return Collections.emptyList();
        ExecutorService pool = Executors.newFixedThreadPool(Math.min(6, sites.size()));
        List<Future<List<MediaItem>>> futures = new ArrayList<>();
        for (Site site : sites.subList(0, Math.min(12, sites.size()))) futures.add(pool.submit(() -> query(site, null, true)));
        List<MediaItem> result = new ArrayList<>();
        for (Future<List<MediaItem>> future : futures) {
            try { result.addAll(future.get(13, TimeUnit.SECONDS)); } catch (Exception ignored) { future.cancel(true); }
            if (result.size() >= limit) break;
        }
        pool.shutdownNow();
        return result.subList(0, Math.min(limit, result.size()));
    }

    public MediaDetail detail(Site site, String id) throws IOException {
        JsonObject root = site.isCsp() ? requireCsp().detail(site, id) : request(site.api, "detail", null, id, null);
        JsonArray list = array(root, "list");
        if (list == null || list.size() == 0) throw new IOException("该接口没有返回影片详情");
        JsonObject vod = list.get(0).getAsJsonObject();
        MediaDetail detail = new MediaDetail();
        detail.item = item(site, vod);
        detail.content = html(text(vod, "vod_content"));
        detail.actor = text(vod, "vod_actor");
        detail.director = text(vod, "vod_director");
        String[] lineNames = split(text(vod, "vod_play_from"), "\\$\\$\\$");
        String[] lineValues = split(text(vod, "vod_play_url"), "\\$\\$\\$");
        for (int i = 0; i < lineValues.length; i++) {
            PlayLine line = new PlayLine();
            line.name = i < lineNames.length && !lineNames[i].trim().isEmpty() ? lineNames[i] : "线路 " + (i + 1);
            for (String raw : lineValues[i].split("#")) {
                int separator = raw.indexOf('$');
                if (separator <= 0 || separator >= raw.length() - 1) continue;
                line.episodes.add(new Episode(raw.substring(0, separator).trim(), raw.substring(separator + 1).trim()));
            }
            if (!line.episodes.isEmpty()) detail.lines.add(line);
        }
        return detail;
    }

    public String resolvePlayback(Site site, String flag, String id) throws IOException {
        return resolvePlaybackResult(site, flag, id).url;
    }

    public ResolvedPlayback resolvePlaybackResult(Site site, String flag, String id) throws IOException {
        if (!site.isCsp()) return new ResolvedPlayback(id, false);
        JsonObject result = requireCsp().player(site, flag, id);
        String address = first(text(result, "url"), text(result, "playUrl"));
        if (address == null || address.trim().isEmpty()) throw new IOException("Spider 没有返回播放地址");
        JsonElement parse = result.get("parse");
        boolean requiresParser = parse != null && !parse.isJsonNull() &&
                ((parse.isJsonPrimitive() && parse.getAsJsonPrimitive().isNumber() && parse.getAsInt() != 0) ||
                 (parse.isJsonPrimitive() && parse.getAsJsonPrimitive().isString() &&
                         !parse.getAsString().trim().isEmpty() && !"0".equals(parse.getAsString().trim())));
        return new ResolvedPlayback(address.trim(), requiresParser);
    }

    private List<MediaItem> query(Site site, String keyword, boolean latest) throws IOException {
        List<MediaItem> result = new ArrayList<>();
        if (site.isCsp()) {
            addItems(result, site, keyword == null ? requireCsp().home(site) : requireCsp().search(site, keyword));
            return result;
        }
        try {
            addItems(result, site, request(site.api, "detail", keyword, null, null));
        } catch (IOException directSearchFailure) {
            if (keyword == null) throw directSearchFailure;
            // Some CMS providers deliberately disable wd search. Scan the newest
            // pages locally so the source still remains useful without inventing
            // or preinstalling a different content provider.
            for (int page = 1; page <= 3; page++) addItems(result, site, request(site.api, "detail", null, null, page));
            String needle = normalize(keyword);
            for (Iterator<MediaItem> iterator = result.iterator(); iterator.hasNext();) {
                if (!normalize(iterator.next().name).contains(needle)) iterator.remove();
            }
        }
        return result;
    }

    private CspSpiderRuntime requireCsp() throws IOException {
        if (csp == null) throw new IOException("当前运行环境不支持 Android CSP Spider");
        return csp;
    }

    private static void addItems(List<MediaItem> target, Site site, JsonObject root) {
        JsonArray list = array(root, "list");
        if (list == null) return;
        for (JsonElement value : list) if (value.isJsonObject()) target.add(item(site, value.getAsJsonObject()));
    }

    private JsonObject request(String api, String action, String keyword, String ids, Integer page) throws IOException {
        HttpUrl base = HttpUrl.parse(api);
        if (base == null) throw new IOException("接口地址无效");
        HttpUrl.Builder builder = base.newBuilder().setQueryParameter("ac", action);
        if (keyword != null) builder.setQueryParameter("wd", keyword);
        if (ids != null) builder.setQueryParameter("ids", ids);
        if (page != null) builder.setQueryParameter("pg", Integer.toString(page));
        Request request = new Request.Builder().url(builder.build())
                .header("User-Agent", "okhttp/4.12 MTPlayer Android")
                .header("Accept", "application/json,text/plain,*/*").build();
        try (Response response = http.newCall(request).execute()) {
            if (!response.isSuccessful() || response.body() == null) throw new IOException("HTTP " + response.code());
            String body = response.body().string().trim();
            JsonElement parsed = gson.fromJson(body, JsonElement.class);
            if (parsed == null || !parsed.isJsonObject()) throw new IOException("接口返回格式无效");
            return parsed.getAsJsonObject();
        } catch (RuntimeException ex) {
            throw new IOException("接口解析失败", ex);
        }
    }

    private static MediaItem item(Site site, JsonObject vod) {
        MediaItem item = new MediaItem();
        item.siteKey = site.key; item.siteName = site.name;
        item.id = first(text(vod, "vod_id"), text(vod, "id"));
        item.name = first(text(vod, "vod_name"), text(vod, "name"), "未命名影片");
        item.poster = first(text(vod, "vod_pic"), text(vod, "pic"));
        item.remarks = first(text(vod, "vod_remarks"), text(vod, "remarks"), "");
        item.year = first(text(vod, "vod_year"), text(vod, "year"), "");
        item.type = first(text(vod, "type_name"), text(vod, "vod_class"), "影视");
        return item;
    }

    private static JsonArray array(JsonObject object, String key) { JsonElement e = object.get(key); return e != null && e.isJsonArray() ? e.getAsJsonArray() : null; }
    private static String text(JsonObject object, String key) { JsonElement e = object.get(key); return e != null && !e.isJsonNull() && e.isJsonPrimitive() ? e.getAsString() : null; }
    private static String first(String... values) { for (String value : values) if (value != null && !value.trim().isEmpty()) return value; return null; }
    private static String[] split(String value, String regex) { return value == null || value.trim().isEmpty() ? new String[0] : value.split(regex, -1); }
    private static String html(String value) { return value == null ? "" : value.replaceAll("<[^>]*>", "").replace("&nbsp;", " ").trim(); }
    private static String normalize(String value) { return value == null ? "" : value.replaceAll("[\\p{P}\\p{Z}]", "").toUpperCase(Locale.ROOT); }
}
