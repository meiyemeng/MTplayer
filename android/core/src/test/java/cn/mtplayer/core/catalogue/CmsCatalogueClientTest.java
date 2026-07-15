package cn.mtplayer.core.catalogue;

import static org.junit.Assert.assertEquals;
import java.util.List;
import org.junit.After;
import org.junit.Before;
import org.junit.Test;
import cn.mtplayer.core.model.MediaDetail;
import cn.mtplayer.core.model.MediaItem;
import cn.mtplayer.core.model.Site;
import okhttp3.OkHttpClient;
import okhttp3.mockwebserver.Dispatcher;
import okhttp3.mockwebserver.MockResponse;
import okhttp3.mockwebserver.MockWebServer;
import okhttp3.mockwebserver.RecordedRequest;

public class CmsCatalogueClientTest {
    private MockWebServer server;
    private CmsCatalogueClient client;
    private Site site;

    @Before public void setup() throws Exception {
        server = new MockWebServer();
        server.setDispatcher(new Dispatcher() {
            @Override public MockResponse dispatch(RecordedRequest request) {
                String query = request.getRequestUrl().query();
                if (query != null && query.contains("ids=1")) return json("{\"list\":[{\"vod_id\":1,\"vod_name\":\"仙逆\",\"vod_content\":\"<p>简介</p>\",\"vod_play_from\":\"lz$$$ff\",\"vod_play_url\":\"第01集$https://cdn/a.m3u8#第02集$https://cdn/b.m3u8$$$HD$https://cdn/c.mp4\"}]}");
                if (query != null && query.contains("wd=")) return new MockResponse().setResponseCode(200).setBody("暂不支持搜索");
                return json("{\"list\":[{\"vod_id\":1,\"vod_name\":\"仙逆\",\"vod_pic\":\"https://img/1.jpg\",\"vod_remarks\":\"更新\"}]}");
            }
        });
        server.start();
        client = new CmsCatalogueClient(new OkHttpClient());
        site = new Site("group:cms", "可用接口", server.url("/api.php/provide/vod/").toString());
    }

    @After public void close() throws Exception { server.shutdown(); }

    @Test public void disabled_remote_search_falls_back_to_recent_pages() {
        List<MediaItem> items = client.searchAll(List.of(site), "仙逆");
        assertEquals(1, items.size());
        assertEquals("可用接口", items.get(0).siteName);
    }

    @Test public void detail_preserves_lines_and_episodes() throws Exception {
        MediaDetail detail = client.detail(site, "1");
        assertEquals(2, detail.lines.size());
        assertEquals(2, detail.lines.get(0).episodes.size());
        assertEquals("https://cdn/a.m3u8", detail.lines.get(0).episodes.get(0).url);
    }

    private static MockResponse json(String body) { return new MockResponse().setResponseCode(200).setHeader("Content-Type", "application/json").setBody(body); }
}
