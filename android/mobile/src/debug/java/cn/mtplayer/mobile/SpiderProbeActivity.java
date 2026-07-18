package cn.mtplayer.mobile;

import android.app.Activity;
import android.os.Bundle;
import android.util.Log;

import com.google.gson.JsonArray;
import com.google.gson.JsonObject;

import java.util.concurrent.TimeUnit;

import cn.mtplayer.core.model.Site;
import cn.mtplayer.core.spider.CspSpiderRuntime;
import okhttp3.OkHttpClient;

/** Debug-only end-to-end probe; it is excluded from release APKs. */
public final class SpiderProbeActivity extends Activity {
    @Override protected void onCreate(Bundle state) {
        super.onCreate(state);
        new Thread(() -> {
            try {
                OkHttpClient http = new OkHttpClient.Builder()
                        .connectTimeout(15, TimeUnit.SECONDS).readTimeout(40, TimeUnit.SECONDS).build();
                Site site = new Site("fish:VideoX", "小北云视", "csp_VideoX", 3,
                        "https://6800.kstore.vip/fish07170158.jar;md5;e562ad98d08e7be755d4a57c22723fb3", "", true);
                CspSpiderRuntime runtime = new CspSpiderRuntime(this, http);
                JsonObject result = runtime.search(site, "仙逆");
                JsonArray search = result.getAsJsonArray("list");
                if (search == null || search.isEmpty()) throw new IllegalStateException("search returned no items");
                String id = search.get(0).getAsJsonObject().get("vod_id").getAsString();
                JsonObject detail = runtime.detail(site, id);
                JsonObject video = detail.getAsJsonArray("list").get(0).getAsJsonObject();
                String flag = video.get("vod_play_from").getAsString().split("\\$\\$\\$", -1)[0];
                String episode = video.get("vod_play_url").getAsString().split("\\$\\$\\$", -1)[0].split("#", -1)[0];
                int separator = episode.indexOf('$');
                String playId = separator >= 0 ? episode.substring(separator + 1) : episode;
                JsonObject player = runtime.player(site, flag, playId);
                String url = player.has("url") ? player.get("url").getAsString() : "";
                if (url.trim().isEmpty()) throw new IllegalStateException("player returned no url");
                Log.i("MTPLAYER_SPIDER_PROBE", "PASS search=" + search.size() + " detail=" + id + " player=" + url);
            } catch (Throwable error) {
                Log.e("MTPLAYER_SPIDER_PROBE", "FAIL " + error, error);
            } finally { finish(); }
        }, "spider-probe").start();
    }
}
