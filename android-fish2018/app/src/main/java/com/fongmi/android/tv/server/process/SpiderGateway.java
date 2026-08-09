package com.fongmi.android.tv.server.process;

import android.text.TextUtils;

import com.fongmi.android.tv.Constant;
import com.fongmi.android.tv.api.LiveApi;
import com.fongmi.android.tv.api.SiteApi;
import com.fongmi.android.tv.api.config.LiveConfig;
import com.fongmi.android.tv.api.config.VodConfig;
import com.fongmi.android.tv.api.parser.LiveParser;
import com.fongmi.android.tv.bean.Config;
import com.fongmi.android.tv.bean.Channel;
import com.fongmi.android.tv.bean.Group;
import com.fongmi.android.tv.bean.Live;
import com.fongmi.android.tv.bean.Result;
import com.fongmi.android.tv.bean.Site;
import com.fongmi.android.tv.impl.Callback;
import com.fongmi.android.tv.impl.ParseCallback;
import com.fongmi.android.tv.player.ParseJob;
import com.fongmi.android.tv.player.Source;
import com.fongmi.android.tv.server.Nano;
import com.fongmi.android.tv.server.Server;
import com.fongmi.android.tv.server.impl.Process;
import com.github.catvod.crawler.Spider;
import com.github.catvod.crawler.SpiderDebug;
import com.github.catvod.net.OkHttp;
import com.github.catvod.utils.Prefers;
import com.google.gson.JsonElement;
import com.google.gson.JsonArray;
import com.google.gson.JsonObject;
import com.google.gson.JsonParser;

import java.nio.charset.StandardCharsets;
import java.io.BufferedReader;
import java.io.ByteArrayInputStream;
import java.io.ByteArrayOutputStream;
import java.io.FilterInputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.InputStreamReader;
import java.net.HttpURLConnection;
import java.net.Proxy;
import java.net.URL;
import java.util.HashMap;
import java.util.List;
import java.util.Map;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicReference;

import fi.iki.elonen.NanoHTTPD;
import fi.iki.elonen.NanoHTTPD.IHTTPSession;
import fi.iki.elonen.NanoHTTPD.Response;

/**
 * Executes TVBox type-3 Spider sites for MTPlayer web and Windows clients.
 *
 * The DEX/JAR stays inside the Android process. Callers only receive the same
 * JSON contract returned by the upstream Spider methods.
 */
public class SpiderGateway implements Process {

    public static final String DEFAULT_TOKEN = "mtplayer-local";
    private static final String PREFIX = "/v1/spider/";
    private static final String TOKEN_KEY = "spider_gateway_token";

    @Override
    public boolean isRequest(IHTTPSession session, String url) {
        return url.startsWith(PREFIX);
    }

    @Override
    public Response doResponse(IHTTPSession session, String url, Map<String, String> files) {
        if (url.equals(PREFIX + "health")) return health();
        if (url.startsWith(PREFIX + "native")) return nativeResponse(session, url);
        if (session.getMethod() != NanoHTTPD.Method.POST)
            return error(Response.Status.METHOD_NOT_ALLOWED, "POST required");
        if (!authorized(session))
            return error(Response.Status.UNAUTHORIZED, "Invalid Spider Gateway token");
        try {
            JsonObject payload = payload(files);
            String method = url.substring(PREFIX.length());
            if (method.equals("config")) return json(loadConfig(payload));
            if (method.equals("live")) return json(live());
            Site site = site(payload);
            String json = invoke(method, site, payload);
            return json(rewriteLocalAddress(json));
        } catch (Throwable e) {
            SpiderDebug.log("spider-gateway", e);
            return error(Response.Status.BAD_REQUEST, concise(e));
        }
    }

    private Response health() {
        JsonObject object = new JsonObject();
        object.addProperty("ok", true);
        object.addProperty("runtime", "android-tvbox");
        object.addProperty("address", Server.get().getAddress(false));
        return json(object.toString());
    }

    private String invoke(String method, Site site, JsonObject payload) throws Exception {
        Spider spider = site.recent().spider();
        return switch (method) {
            case "home" -> home(site);
            case "category" -> spider.categoryContent(
                    string(payload, "tid"),
                    string(payload, "page", "1"),
                    bool(payload, "filter", true),
                    stringMap(payload.get("extend")));
            case "search" -> {
                String page = string(payload, "page", "1");
                String keyword = string(payload, "keyword");
                yield "1".equals(page)
                        ? spider.searchContent(keyword, bool(payload, "quick", false))
                        : spider.searchContent(keyword, bool(payload, "quick", false), page);
            }
            case "detail" -> detail(spider, payload);
            case "player" -> player(site, spider, payload);
            case "action" -> spider.action(string(payload, "action"));
            default -> throw new IllegalArgumentException("Unsupported Spider method: " + method);
        };
    }

    private String home(Site site) throws Exception {
        Result home = SiteApi.homeContent(site);
        return home.toString();
    }

    private String detail(Spider spider, JsonObject payload) throws Exception {
        Result result = Result.fromJson(spider.detailContent(List.of(string(payload, "id"))));
        Source.get().parse(result.getVod().setFlags());
        return result.toString();
    }

    private String player(Site site, Spider spider, JsonObject payload) throws Exception {
        String flag = string(payload, "flag");
        Result result = Result.fromJson(spider.playerContent(flag, string(payload, "id"), VodConfig.get().getFlags()));
        if (result.getFlag().isEmpty()) result.setFlag(flag);
        result.setUrl(Source.get().fetch(result));
        result.setHeader(site.getHeader());
        result.setKey(site.getKey());
        return resolvePlayer(result);
    }

    private String loadConfig(JsonObject payload) throws Exception {
        String url = string(payload, "url");
        if (TextUtils.isEmpty(url)) throw new IllegalArgumentException("Missing config URL");
        Config config = Config.find(url, 0).name(string(payload, "name", "MTPlayer Gateway")).save();
        CountDownLatch latch = new CountDownLatch(1);
        AtomicReference<String> failure = new AtomicReference<>("");
        VodConfig.load(config, new Callback() {
            @Override
            public void success() {
                latch.countDown();
            }

            @Override
            public void error(String msg) {
                failure.set(TextUtils.isEmpty(msg) ? "Load config failed" : msg);
                latch.countDown();
            }
        });
        if (!latch.await(120, TimeUnit.SECONDS)) throw new IllegalStateException("Load config timed out");
        if (!failure.get().isEmpty()) throw new IllegalStateException(failure.get());
        JsonObject object = new JsonObject();
        object.addProperty("ok", true);
        object.addProperty("url", VodConfig.getUrl());
        object.addProperty("sites", VodConfig.get().getSites().size());
        object.addProperty("parses", VodConfig.get().getParses().size());
        object.addProperty("lives", LiveConfig.get().getLives().size());
        return object.toString();
    }

    private String live() {
        LiveConfig.get().ensureLoaded();
        JsonArray items = new JsonArray();
        JsonArray sources = new JsonArray();
        JsonArray errors = new JsonArray();
        for (Live live : LiveConfig.get().getLives()) {
            int rawLength = 0;
            try {
                LiveApi.parse(live);
                if (live.getGroups().isEmpty() && live.getApi().isEmpty()) {
                    String text = localString(live);
                    rawLength = text.length();
                    if (!text.isEmpty()) LiveParser.text(live, text);
                }
            } catch (Throwable e) {
                JsonObject error = new JsonObject();
                error.addProperty("name", live.getName());
                error.addProperty("message", concise(e));
                errors.add(error);
            }
            JsonObject source = new JsonObject();
            source.addProperty("name", live.getName());
            source.addProperty("url", live.getUrl());
            source.addProperty("api", live.getApi());
            source.addProperty("jar", live.getJar());
            source.addProperty("groups", live.getGroups().size());
            source.addProperty("rawLength", rawLength);
            sources.add(source);
            for (Group group : live.getGroups()) {
                if (group.isKeep() || group.isHidden()) continue;
                for (Channel channel : group.getChannel()) {
                    if (channel.getUrls().isEmpty()) continue;
                    JsonObject item = new JsonObject();
                    item.addProperty("name", channel.getName());
                    item.addProperty("group", group.getName());
                    item.addProperty("address", rewriteNativeAddress(channel.getCurrent()));
                    item.addProperty("logoAddress", rewriteNativeAddress(channel.getLogo()));
                    item.addProperty("epgAddress", channel.getEpg());
                    item.add("headers", new com.google.gson.Gson().toJsonTree(channel.getHeaders()));
                    item.addProperty("lineCount", channel.getUrls().size());
                    items.add(item);
                }
            }
        }
        JsonObject result = new JsonObject();
        result.add("channels", items);
        result.add("sources", sources);
        result.add("errors", errors);
        return result.toString();
    }

    private Response nativeResponse(IHTTPSession session, String url) {
        if (session.getMethod() != NanoHTTPD.Method.GET && session.getMethod() != NanoHTTPD.Method.HEAD)
            return error(Response.Status.METHOD_NOT_ALLOWED, "GET required");
        String path = url.substring((PREFIX + "native").length());
        if (!path.startsWith("/iptv/play/") &&
                !path.startsWith("/iptv/media") &&
                !path.startsWith("/iptv/logo/"))
            return error(Response.Status.FORBIDDEN, "Unsupported native media path");
        HttpURLConnection connection = null;
        try {
            String query = session.getQueryParameterString();
            URL target = new URL("http://127.0.0.1:5266" + path +
                    (TextUtils.isEmpty(query) ? "" : "?" + query));
            connection = (HttpURLConnection) target.openConnection(Proxy.NO_PROXY);
            connection.setConnectTimeout(15000);
            connection.setReadTimeout(60000);
            connection.setInstanceFollowRedirects(true);
            copyRequestHeader(session, connection, "range");
            copyRequestHeader(session, connection, "user-agent");
            copyRequestHeader(session, connection, "referer");
            int code = connection.getResponseCode();
            InputStream input = code >= 400 ? connection.getErrorStream() : connection.getInputStream();
            if (input == null) {
                connection.disconnect();
                return error(Response.Status.INTERNAL_ERROR, "Native media returned no body");
            }
            String mime = connection.getContentType();
            if (isManifest(path, mime)) {
                byte[] bytes = readAll(input);
                input.close();
                connection.disconnect();
                String manifest = new String(bytes, StandardCharsets.UTF_8)
                        .replace("http://127.0.0.1:5266", PREFIX + "native")
                        .replace("http://localhost:5266", PREFIX + "native");
                byte[] rewritten = manifest.getBytes(StandardCharsets.UTF_8);
                Response response = NanoHTTPD.newFixedLengthResponse(
                        status(code),
                        TextUtils.isEmpty(mime) ? "application/vnd.apple.mpegurl" : mime,
                        new ByteArrayInputStream(rewritten),
                        rewritten.length);
                return mediaHeaders(response);
            }
            HttpURLConnection active = connection;
            Response response = NanoHTTPD.newChunkedResponse(
                    status(code),
                    TextUtils.isEmpty(mime) ? "application/octet-stream" : mime,
                    new FilterInputStream(input) {
                        @Override
                        public void close() throws IOException {
                            try {
                                super.close();
                            } finally {
                                active.disconnect();
                            }
                        }
                    });
            String contentRange = connection.getHeaderField("Content-Range");
            String acceptRanges = connection.getHeaderField("Accept-Ranges");
            if (!TextUtils.isEmpty(contentRange)) response.addHeader("Content-Range", contentRange);
            if (!TextUtils.isEmpty(acceptRanges)) response.addHeader("Accept-Ranges", acceptRanges);
            return mediaHeaders(response);
        } catch (Throwable e) {
            if (connection != null) connection.disconnect();
            SpiderDebug.log("spider-native", e);
            return error(Response.Status.INTERNAL_ERROR, concise(e));
        }
    }

    private void copyRequestHeader(IHTTPSession session, HttpURLConnection connection, String name) {
        String value = session.getHeaders().get(name);
        if (!TextUtils.isEmpty(value)) connection.setRequestProperty(name, value);
    }

    private boolean isManifest(String path, String mime) {
        return path.toLowerCase().contains(".m3u8") ||
                (!TextUtils.isEmpty(mime) && mime.toLowerCase().contains("mpegurl"));
    }

    private byte[] readAll(InputStream input) throws IOException {
        ByteArrayOutputStream output = new ByteArrayOutputStream();
        byte[] buffer = new byte[16384];
        int count;
        while ((count = input.read(buffer)) != -1) output.write(buffer, 0, count);
        return output.toByteArray();
    }

    private Response.IStatus status(int code) {
        Response.Status value = Response.Status.lookup(code);
        return value == null ? Response.Status.OK : value;
    }

    private Response mediaHeaders(Response response) {
        response.addHeader("Cache-Control", "no-store");
        response.addHeader("Access-Control-Allow-Origin", "*");
        response.addHeader("X-Content-Type-Options", "nosniff");
        return response;
    }

    private String localString(Live live) throws Exception {
        String url = live.getUrl();
        if (!url.startsWith("http://127.0.0.1:") && !url.startsWith("http://localhost:"))
            return OkHttp.string(url, live.getHeaders());
        HttpURLConnection connection = (HttpURLConnection) new URL(url).openConnection(Proxy.NO_PROXY);
        connection.setConnectTimeout(15000);
        connection.setReadTimeout(30000);
        for (Map.Entry<String, String> header : live.getHeaders().entrySet())
            connection.setRequestProperty(header.getKey(), header.getValue());
        StringBuilder builder = new StringBuilder();
        try (BufferedReader reader = new BufferedReader(new InputStreamReader(connection.getInputStream(), StandardCharsets.UTF_8))) {
            String line;
            while ((line = reader.readLine()) != null) builder.append(line).append('\n');
        } finally {
            connection.disconnect();
        }
        return builder.toString();
    }

    private String resolvePlayer(Result result) throws Exception {
        boolean parse = result.needParse() || result.shouldUseParse();
        if (!parse) return playerJson(result, result.getRealUrl(), result.getHeader(), "");
        CountDownLatch latch = new CountDownLatch(1);
        AtomicReference<String> url = new AtomicReference<>("");
        AtomicReference<String> from = new AtomicReference<>("");
        AtomicReference<Map<String, String>> headers = new AtomicReference<>(result.getHeader());
        ParseJob job = ParseJob.create(new ParseCallback() {
            @Override
            public void onParseSuccess(Map<String, String> value, String finalUrl, String parser) {
                headers.set(value);
                url.set(finalUrl);
                from.set(parser);
                latch.countDown();
            }

            @Override
            public void onParseError() {
                latch.countDown();
            }
        }).start(result, result.shouldUseParse());
        boolean finished = latch.await(Constant.TIMEOUT_PARSE_DEF + 5000, TimeUnit.MILLISECONDS);
        if (!finished) job.stop();
        if (url.get().isEmpty()) throw new IllegalStateException("Unable to resolve playable media URL");
        return playerJson(result, url.get(), headers.get(), from.get());
    }

    private String playerJson(Result result, String url, Map<String, String> headers, String parser) {
        JsonObject object = JsonParser.parseString(result.toString()).getAsJsonObject();
        object.addProperty("url", rewriteLocalAddress(url));
        object.addProperty("parse", 0);
        object.addProperty("jx", 0);
        object.addProperty("playUrl", "");
        object.add("header", new com.google.gson.Gson().toJsonTree(headers));
        if (!TextUtils.isEmpty(parser)) object.addProperty("jxFrom", parser);
        return object.toString();
    }

    private Site site(JsonObject payload) {
        JsonElement element = payload.get("site");
        if (element == null || !element.isJsonObject())
            throw new IllegalArgumentException("Missing site");
        JsonObject object = element.getAsJsonObject().deepCopy();
        String key = string(object, "key");
        Site loaded = VodConfig.get().getSite(key);
        int separator = key.indexOf(':');
        if ((loaded.isEmpty() || loaded.getType() != 3) && separator >= 0 && separator + 1 < key.length())
            loaded = VodConfig.get().getSite(key.substring(separator + 1));
        if (!loaded.isEmpty() && loaded.getType() == 3 && loaded.getApi().startsWith("csp_"))
            return loaded;
        normalizeInt(object, "searchable");
        normalizeInt(object, "quickSearch");
        normalizeInt(object, "filterable");
        normalizeInt(object, "changeable");
        String jar = string(object, "jar");
        Site site = Site.objectFrom(object, jar);
        if (site.isEmpty() || site.getType() != 3 || !site.getApi().startsWith("csp_"))
            throw new IllegalArgumentException("Only TVBox csp_* sites are supported");
        if (TextUtils.isEmpty(site.getJar()))
            throw new IllegalArgumentException("Missing Spider JAR");
        return site;
    }

    private void normalizeInt(JsonObject object, String name) {
        JsonElement value = object.get(name);
        if (value == null || !value.isJsonPrimitive() || !value.getAsJsonPrimitive().isBoolean()) return;
        object.addProperty(name, value.getAsBoolean() ? 1 : 0);
    }

    private JsonObject payload(Map<String, String> files) {
        String body = files == null ? "" : files.get("postData");
        if (TextUtils.isEmpty(body)) throw new IllegalArgumentException("Empty JSON body");
        JsonElement parsed = JsonParser.parseString(body);
        if (!parsed.isJsonObject()) throw new IllegalArgumentException("JSON object required");
        return parsed.getAsJsonObject();
    }

    private boolean authorized(IHTTPSession session) {
        String authorization = session.getHeaders().get("authorization");
        String expected = Prefers.getString(TOKEN_KEY, DEFAULT_TOKEN).trim();
        return !expected.isEmpty() && ("Bearer " + expected).equals(authorization);
    }

    private String rewriteLocalAddress(String text) {
        String local = Server.get().getAddress(true);
        String address = Server.get().getAddress(false);
        return text
                .replace(local, address)
                .replace("http://127.0.0.1:9978", address)
                .replace("http://localhost:9978", address);
    }

    private String rewriteNativeAddress(String text) {
        if (TextUtils.isEmpty(text)) return text;
        return rewriteLocalAddress(text)
                .replace("http://127.0.0.1:5266", PREFIX + "native")
                .replace("http://localhost:5266", PREFIX + "native");
    }

    private HashMap<String, String> stringMap(JsonElement element) {
        HashMap<String, String> result = new HashMap<>();
        if (element == null || !element.isJsonObject()) return result;
        for (Map.Entry<String, JsonElement> entry : element.getAsJsonObject().entrySet())
            result.put(entry.getKey(), entry.getValue().isJsonNull() ? "" : entry.getValue().getAsString());
        return result;
    }

    private String string(JsonObject object, String name) {
        return string(object, name, "");
    }

    private String string(JsonObject object, String name, String fallback) {
        JsonElement value = object.get(name);
        return value == null || value.isJsonNull() ? fallback : value.getAsString();
    }

    private boolean bool(JsonObject object, String name, boolean fallback) {
        JsonElement value = object.get(name);
        return value == null || value.isJsonNull() ? fallback : value.getAsBoolean();
    }

    private Response json(String text) {
        Response response = NanoHTTPD.newFixedLengthResponse(Response.Status.OK, "application/json; charset=utf-8", text);
        response.addHeader("Cache-Control", "no-store");
        return response;
    }

    private Response error(Response.Status status, String message) {
        JsonObject object = new JsonObject();
        object.addProperty("ok", false);
        object.addProperty("message", message);
        return NanoHTTPD.newFixedLengthResponse(status, "application/json; charset=utf-8", object.toString());
    }

    private String concise(Throwable error) {
        Throwable cause = error.getCause() == null ? error : error.getCause();
        String message = cause.getMessage();
        if (TextUtils.isEmpty(message)) message = cause.getClass().getSimpleName();
        byte[] bytes = message.getBytes(StandardCharsets.UTF_8);
        if (bytes.length <= 600) return message;
        return new String(bytes, 0, 600, StandardCharsets.UTF_8) + "...";
    }
}
