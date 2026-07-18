package cn.mtplayer.core.spider;

import android.content.Context;
import android.content.SharedPreferences;

import com.google.gson.Gson;
import com.google.gson.JsonObject;

import java.io.BufferedInputStream;
import java.io.BufferedOutputStream;
import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.ServerSocket;
import java.net.Socket;
import java.nio.charset.StandardCharsets;
import java.security.SecureRandom;
import java.util.Locale;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

import cn.mtplayer.core.model.Site;
import okhttp3.OkHttpClient;

/**
 * Optional LAN gateway that lets desktop/web clients delegate Android-only
 * TVBox csp_* execution to a running MTPlayer Android client.
 */
public final class SpiderGatewayServer {
    public static final int DEFAULT_PORT = 9978;
    private static final int MAX_BODY_BYTES = 1024 * 1024;
    private static final int MAX_HEADER_LINE_BYTES = 8192;
    private final SharedPreferences preferences;
    private final CspSpiderRuntime runtime;
    private final Gson gson = new Gson();
    private final ExecutorService clients = Executors.newCachedThreadPool();
    private volatile ServerSocket server;
    private volatile Thread acceptThread;

    public SpiderGatewayServer(Context context, OkHttpClient http) {
        Context application = context.getApplicationContext();
        preferences = application.getSharedPreferences("mtplayer.spider.gateway", Context.MODE_PRIVATE);
        runtime = new CspSpiderRuntime(application, http);
        ensureToken();
    }

    public boolean isEnabled() { return preferences.getBoolean("enabled", false); }
    public int port() { return preferences.getInt("port", DEFAULT_PORT); }
    public String token() { return preferences.getString("token", ""); }

    public synchronized void setEnabled(boolean enabled) {
        preferences.edit().putBoolean("enabled", enabled).apply();
        if (enabled) start(); else stop();
    }

    public synchronized void startIfEnabled() { if (isEnabled()) start(); }

    public synchronized void start() {
        if (server != null && !server.isClosed()) return;
        try {
            server = new ServerSocket(port());
            acceptThread = new Thread(this::acceptLoop, "mtplayer-spider-gateway");
            acceptThread.setDaemon(true);
            acceptThread.start();
        } catch (IOException error) {
            stop();
            throw new IllegalStateException("无法启动 Spider Gateway：" + message(error), error);
        }
    }

    public synchronized void stop() {
        ServerSocket current = server;
        server = null;
        if (current != null) try { current.close(); } catch (IOException ignored) { }
        Thread thread = acceptThread;
        acceptThread = null;
        if (thread != null) thread.interrupt();
    }

    private void acceptLoop() {
        while (server != null && !server.isClosed()) {
            try {
                Socket socket = server.accept();
                socket.setSoTimeout(60_000);
                clients.execute(() -> handle(socket));
            } catch (IOException error) {
                if (server != null && !server.isClosed()) android.util.Log.w("MTPlayer-Gateway", message(error), error);
            }
        }
    }

    private void handle(Socket socket) {
        try (socket;
             BufferedInputStream input = new BufferedInputStream(socket.getInputStream());
             BufferedOutputStream output = new BufferedOutputStream(socket.getOutputStream())) {
            String requestLine = readLine(input);
            if (requestLine == null) return;
            String[] requestParts = requestLine.split(" ", 3);
            if (requestParts.length < 2 || !"POST".equals(requestParts[0])) { write(output, 405, error("只支持 POST 请求")); return; }
            int length = 0;
            String authorization = "";
            for (String line; (line = readLine(input)) != null && !line.isEmpty(); ) {
                int split = line.indexOf(':');
                if (split < 0) continue;
                String name = line.substring(0, split).trim().toLowerCase(Locale.ROOT);
                String value = line.substring(split + 1).trim();
                if ("content-length".equals(name)) {
                    try { length = Integer.parseInt(value); }
                    catch (NumberFormatException error) { write(output, 400, error("无效的 Content-Length")); return; }
                }
                if ("authorization".equals(name)) authorization = value;
            }
            if (!authorization.equals("Bearer " + token())) { write(output, 401, error("Gateway 令牌无效")); return; }
            if (length < 0 || length > MAX_BODY_BYTES) { write(output, 413, error("请求内容过大")); return; }
            byte[] body = readExactly(input, length);
            if (body.length != length) { write(output, 400, error("请求内容不完整")); return; }
            int bodyOffset = body.length >= 3 && (body[0] & 0xff) == 0xef && (body[1] & 0xff) == 0xbb && (body[2] & 0xff) == 0xbf ? 3 : 0;
            GatewayRequest request = gson.fromJson(new String(body, bodyOffset, body.length - bodyOffset, StandardCharsets.UTF_8), GatewayRequest.class);
            if (request == null || request.site == null || !request.site.isCsp()) { write(output, 400, error("缺少有效的 CSP 站点")); return; }
            JsonObject result;
            String path = requestParts[1];
            if (path.endsWith("/home")) result = runtime.home(request.site);
            else if (path.endsWith("/search")) result = runtime.search(request.site, request.keyword == null ? "" : request.keyword);
            else if (path.endsWith("/detail")) result = runtime.detail(request.site, request.id == null ? "" : request.id);
            else if (path.endsWith("/player")) result = runtime.player(request.site, request.flag, request.id == null ? "" : request.id);
            else { write(output, 404, error("未知的 Gateway 方法")); return; }
            write(output, 200, result.toString());
        } catch (Exception error) {
            try {
                BufferedOutputStream output = new BufferedOutputStream(socket.getOutputStream());
                write(output, 500, error("Spider 执行失败：" + message(error)));
            } catch (IOException ignored) { }
        }
    }

    private static String readLine(InputStream input) throws IOException {
        ByteArrayOutputStream bytes = new ByteArrayOutputStream(128);
        while (bytes.size() <= MAX_HEADER_LINE_BYTES) {
            int value = input.read();
            if (value < 0) return bytes.size() == 0 ? null : bytes.toString(StandardCharsets.UTF_8.name());
            if (value == '\n') return bytes.toString(StandardCharsets.UTF_8.name());
            if (value != '\r') bytes.write(value);
        }
        throw new IOException("HTTP 请求头过长");
    }

    private static byte[] readExactly(InputStream input, int length) throws IOException {
        byte[] result = new byte[length];
        int offset = 0;
        while (offset < length) {
            int read = input.read(result, offset, length - offset);
            if (read < 0) break;
            offset += read;
        }
        if (offset == length) return result;
        byte[] partial = new byte[offset];
        System.arraycopy(result, 0, partial, 0, offset);
        return partial;
    }

    private static void write(OutputStream output, int status, String body) throws IOException {
        byte[] bytes = body.getBytes(StandardCharsets.UTF_8);
        String headers = "HTTP/1.1 " + status + " " + reason(status) + "\r\n"
                + "Content-Type: application/json; charset=utf-8\r\n"
                + "Content-Length: " + bytes.length + "\r\n"
                + "Connection: close\r\n\r\n";
        output.write(headers.getBytes(StandardCharsets.US_ASCII));
        output.write(bytes);
        output.flush();
    }

    private void ensureToken() {
        if (!token().isEmpty()) return;
        byte[] bytes = new byte[24];
        new SecureRandom().nextBytes(bytes);
        StringBuilder value = new StringBuilder(bytes.length * 2);
        for (byte item : bytes) value.append(String.format(Locale.ROOT, "%02x", item & 0xff));
        preferences.edit().putString("token", value.toString()).apply();
    }

    private static String error(String value) {
        return "{\"message\":\"" + value.replace("\\", "\\\\").replace("\"", "\\\"").replace("\n", "\\n") + "\"}";
    }
    private static String reason(int status) { return status == 200 ? "OK" : status == 400 ? "Bad Request" : status == 401 ? "Unauthorized" : status == 404 ? "Not Found" : status == 405 ? "Method Not Allowed" : status == 413 ? "Payload Too Large" : "Internal Server Error"; }
    private static String message(Throwable value) { return value.getMessage() == null ? value.getClass().getSimpleName() : value.getMessage(); }

    private static final class GatewayRequest {
        Site site;
        String keyword;
        String id;
        String flag;
    }
}
