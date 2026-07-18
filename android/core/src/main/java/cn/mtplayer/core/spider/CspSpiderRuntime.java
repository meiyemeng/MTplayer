package cn.mtplayer.core.spider;

import android.content.Context;

import com.google.gson.JsonElement;
import com.google.gson.JsonObject;
import com.google.gson.JsonParser;
import com.google.gson.Strictness;
import com.google.gson.stream.JsonReader;

import java.io.File;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.StringReader;
import java.lang.reflect.InvocationTargetException;
import java.lang.reflect.Method;
import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.util.Collections;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.TimeUnit;

import cn.mtplayer.core.model.Site;
import dalvik.system.DexClassLoader;
import okhttp3.OkHttpClient;
import okhttp3.Request;
import okhttp3.Response;

/** Executes TVBox csp_* plug-ins inside the Android application sandbox. */
public final class CspSpiderRuntime {
    private static final long MAX_JAR_BYTES = 64L * 1024 * 1024;
    private final Context context;
    private final OkHttpClient http;
    private final OkHttpClient jarHttp;
    private final File root;
    private final Map<String, DexClassLoader> loaders = new ConcurrentHashMap<>();
    private final Map<String, Object> spiders = new ConcurrentHashMap<>();

    public CspSpiderRuntime(Context context, OkHttpClient http) {
        this.context = context.getApplicationContext();
        this.http = http;
        this.jarHttp = http.newBuilder()
                .connectTimeout(20, TimeUnit.SECONDS)
                .readTimeout(60, TimeUnit.SECONDS)
                .callTimeout(75, TimeUnit.SECONDS)
                .build();
        this.root = new File(context.getCodeCacheDir(), "mtplayer-spider");
        if (!root.exists() && !root.mkdirs()) throw new IllegalStateException("无法创建 Spider 缓存目录");
    }

    public JsonObject home(Site site) throws IOException {
        JsonObject result = invokeJson(site, "homeVideoContent", new Class<?>[0]);
        if (hasList(result)) return result;
        return invokeJson(site, "homeContent", new Class<?>[]{boolean.class}, false);
    }

    public JsonObject search(Site site, String keyword) throws IOException {
        return invokeJson(site, "searchContent", new Class<?>[]{String.class, boolean.class}, keyword, false);
    }

    public JsonObject detail(Site site, String id) throws IOException {
        return invokeJson(site, "detailContent", new Class<?>[]{List.class}, Collections.singletonList(id));
    }

    public JsonObject player(Site site, String flag, String id) throws IOException {
        return invokeJson(site, "playerContent", new Class<?>[]{String.class, String.class, List.class},
                flag == null ? "" : flag, id, Collections.emptyList());
    }

    private JsonObject invokeJson(Site site, String method, Class<?>[] signature, Object... args) throws IOException {
        try {
            Object spider = spider(site);
            Method target = spider.getClass().getMethod(method, signature);
            Object value = target.invoke(spider, args);
            if (!(value instanceof String) || ((String) value).trim().isEmpty()) return new JsonObject();
            JsonReader reader = new JsonReader(new StringReader((String) value));
            reader.setStrictness(Strictness.LENIENT);
            JsonElement parsed = JsonParser.parseReader(reader);
            if (!parsed.isJsonObject()) throw new IOException("Spider 返回的不是 JSON 对象");
            return parsed.getAsJsonObject();
        } catch (InvocationTargetException exception) {
            Throwable cause = exception.getCause() == null ? exception : exception.getCause();
            throw new IOException("Spider 调用失败：" + message(cause), cause);
        } catch (ReflectiveOperationException | RuntimeException exception) {
            throw new IOException("Spider 运行失败：" + message(exception), exception);
        }
    }

    private Object spider(Site site) throws IOException, ReflectiveOperationException {
        if (site == null || !site.isCsp()) throw new IOException("该站点不是 CSP Spider");
        if (site.jar == null || site.jar.trim().isEmpty()) throw new IOException("配置没有提供 Spider JAR");
        String key = digest(site.jar + "|" + site.key);
        Object cached = spiders.get(key);
        if (cached != null) return cached;
        synchronized (spiders) {
            cached = spiders.get(key);
            if (cached != null) return cached;
            DexClassLoader loader = loader(site.jar);
            String api = site.api.substring("csp_".length());
            Object created = loader.loadClass("com.github.catvod.spider." + api).getDeclaredConstructor().newInstance();
            try { created.getClass().getField("siteKey").set(created, site.key); } catch (NoSuchFieldException ignored) { }
            Method init;
            try {
                init = created.getClass().getMethod("init", Context.class, String.class);
                init.invoke(created, context, site.ext == null ? "" : site.ext);
            } catch (NoSuchMethodException exception) {
                init = created.getClass().getMethod("init", Context.class);
                init.invoke(created, context);
            }
            spiders.put(key, created);
            return created;
        }
    }

    private DexClassLoader loader(String decorated) throws IOException, ReflectiveOperationException {
        DexClassLoader cached = loaders.get(decorated);
        if (cached != null) return cached;
        synchronized (loaders) {
            cached = loaders.get(decorated);
            if (cached != null) return cached;
            String[] parts = decorated.split(";md5;", 2);
            String address = parts[0].trim();
            String expected = parts.length > 1 ? parts[1].trim() : "";
            if (expected.startsWith("http://") || expected.startsWith("https://")) expected = downloadText(expected).trim();
            File jar = new File(root, digest(decorated) + ".jar");
            if (!jar.isFile() || (isHexMd5(expected) && !md5(jar).equalsIgnoreCase(expected))) downloadJar(address, jar);
            if (isHexMd5(expected) && !md5(jar).equalsIgnoreCase(expected)) {
                if (!jar.delete()) jar.deleteOnExit();
                throw new IOException("Spider JAR 的 MD5 校验失败");
            }
            if (!jar.setReadOnly()) throw new IOException("无法锁定 Spider JAR");
            DexClassLoader loader = new DexClassLoader(jar.getAbsolutePath(), root.getAbsolutePath(), root.getAbsolutePath(), context.getClassLoader());
            try {
                Class<?> init = loader.loadClass("com.github.catvod.spider.Init");
                init.getMethod("init", Context.class).invoke(null, context);
            } catch (ClassNotFoundException | NoSuchMethodException ignored) { }
            loaders.put(decorated, loader);
            return loader;
        }
    }

    private void downloadJar(String address, File target) throws IOException {
        Request request = new Request.Builder().url(address).header("User-Agent", "MTPlayer/1.3.1 Android").build();
        File temporary = new File(target.getAbsolutePath() + ".download");
        try (Response response = jarHttp.newCall(request).execute()) {
            if (!response.isSuccessful() || response.body() == null) throw new IOException("Spider JAR 下载失败：HTTP " + response.code());
            if (response.body().contentLength() > MAX_JAR_BYTES) throw new IOException("Spider JAR 超过 64 MiB");
            long total = 0;
            try (InputStream input = response.body().byteStream();
                 FileOutputStream output = new FileOutputStream(temporary)) {
                byte[] buffer = new byte[32 * 1024];
                int read;
                while ((read = input.read(buffer)) >= 0) {
                    total += read;
                    if (total > MAX_JAR_BYTES) throw new IOException("Spider JAR 超过 64 MiB");
                    output.write(buffer, 0, read);
                }
            }
        } catch (IOException exception) {
            temporary.delete();
            throw exception;
        }
        if (target.exists() && !target.delete()) throw new IOException("无法更新 Spider JAR");
        if (!temporary.renameTo(target)) throw new IOException("无法保存 Spider JAR");
    }

    private String downloadText(String address) throws IOException {
        try (Response response = jarHttp.newCall(new Request.Builder().url(address).build()).execute()) {
            if (!response.isSuccessful() || response.body() == null) throw new IOException("MD5 地址读取失败");
            return response.body().string();
        }
    }

    private static boolean hasList(JsonObject value) {
        return value.has("list") && value.get("list").isJsonArray() && value.getAsJsonArray("list").size() > 0;
    }

    private static boolean isHexMd5(String value) { return value != null && value.matches("(?i)[0-9a-f]{32}"); }
    private static String md5(File file) throws IOException {
        try (java.io.FileInputStream input = new java.io.FileInputStream(file)) {
            MessageDigest digest = MessageDigest.getInstance("MD5");
            byte[] buffer = new byte[32 * 1024]; int read;
            while ((read = input.read(buffer)) >= 0) digest.update(buffer, 0, read);
            return hex(digest.digest());
        } catch (java.security.NoSuchAlgorithmException exception) { throw new IOException(exception); }
    }
    private static String digest(String value) {
        try { return hex(MessageDigest.getInstance("SHA-256").digest(value.getBytes(StandardCharsets.UTF_8))).substring(0, 32); }
        catch (java.security.NoSuchAlgorithmException exception) { throw new IllegalStateException(exception); }
    }
    private static String hex(byte[] value) {
        StringBuilder result = new StringBuilder(value.length * 2);
        for (byte b : value) result.append(String.format(Locale.ROOT, "%02x", b & 0xff));
        return result.toString();
    }
    private static String message(Throwable value) { return value.getMessage() == null ? value.getClass().getSimpleName() : value.getMessage(); }
}
