package cn.mtplayer.tv;

import android.content.Intent;
import android.os.Bundle;
import android.view.ViewGroup;
import android.widget.Button;
import android.widget.GridLayout;
import android.widget.ImageView;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;
import android.widget.Toast;

import androidx.appcompat.app.AppCompatActivity;
import androidx.media3.common.util.UnstableApi;

import com.bumptech.glide.Glide;

import java.util.List;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

import cn.mtplayer.core.model.Episode;
import cn.mtplayer.core.catalogue.CmsCatalogueClient;
import cn.mtplayer.core.model.MediaDetail;
import cn.mtplayer.core.model.MediaItem;
import cn.mtplayer.core.model.PlayLine;
import cn.mtplayer.core.model.Site;
import cn.mtplayer.core.player.WebParserActivity;

@UnstableApi
public final class TvDetailActivity extends AppCompatActivity {
    private final ExecutorService executor = Executors.newSingleThreadExecutor();
    private LinearLayout root;
    private MediaItem item;
    private MediaDetail detail;
    private TvLibrary library;
    private Site selectedSite;
    private int lineIndex;
    private Episode selected;

    @Override protected void onCreate(Bundle state) {
        super.onCreate(state);
        item = (MediaItem) getIntent().getSerializableExtra("item");
        library = TvServices.library;
        ScrollView scroll = new ScrollView(this);
        root = TvUi.column(this);
        scroll.addView(root);
        setContentView(scroll);
        root.addView(TvUi.title(this, item == null ? "影片详情" : item.name, 34), TvUi.matchWrap());
        root.addView(TvUi.text(this, "正在检查播放接口…"), TvUi.matchWrap());
        load();
    }

    @Override protected void onDestroy() {
        executor.shutdownNow();
        super.onDestroy();
    }

    private void load() {
        executor.execute(() -> {
            try {
                List<Site> sites = TvServices.configurations.enabledSites();
                for (Site value : sites) if (value.key.equals(item.siteKey)) { selectedSite = value; break; }
                if (selectedSite == null) throw new IllegalStateException("播放接口已停用");
                detail = TvServices.catalogue.detail(selectedSite, item.id);
                runOnUiThread(this::render);
            } catch (Exception error) {
                runOnUiThread(() -> root.addView(TvUi.text(this, "加载失败：" + message(error)), TvUi.matchWrap()));
            }
        });
    }

    private void render() {
        root.removeAllViews();
        LinearLayout hero = new LinearLayout(this);
        ImageView poster = new ImageView(this);
        poster.setScaleType(ImageView.ScaleType.CENTER_CROP);
        Glide.with(this).load(detail.item.poster).into(poster);
        hero.addView(poster, new LinearLayout.LayoutParams(TvUi.dp(this, 300), TvUi.dp(this, 430)));

        LinearLayout info = TvUi.column(this);
        TextView type = TvUi.text(this, detail.item.type + " · " + detail.item.year + " · " + detail.item.siteName);
        type.setTextColor(TvUi.RED);
        info.addView(type, TvUi.matchWrap());
        info.addView(TvUi.title(this, detail.item.name, 38), TvUi.matchWrap());
        info.addView(TvUi.text(this, "导演：" + safe(detail.director) + "\n主演：" + safe(detail.actor) + "\n\n" + safe(detail.content)), TvUi.matchWrap());
        LinearLayout actions = new LinearLayout(this);
        Button play = TvUi.button(this, "▶ 播放所选集", true);
        Button favorite = TvUi.button(this, library.isFavorite(item) ? "♥ 已收藏" : "♡ 加入收藏", false);
        actions.addView(play, new LinearLayout.LayoutParams(TvUi.dp(this, 230), TvUi.dp(this, 64)));
        actions.addView(favorite, new LinearLayout.LayoutParams(TvUi.dp(this, 210), TvUi.dp(this, 64)));
        info.addView(actions, TvUi.matchWrap());
        hero.addView(info, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1));
        root.addView(hero, TvUi.matchWrap());
        favorite.setOnClickListener(view -> {
            library.toggle(item);
            favorite.setText(library.isFavorite(item) ? "♥ 已收藏" : "♡ 加入收藏");
        });

        root.addView(TvUi.title(this, "播放接口", 22), TvUi.matchWrap());
        TextView source = TvUi.text(this, detail.item.siteName + "（已确认包含该影片）");
        source.setTextColor(TvUi.TEXT);
        source.setPadding(TvUi.dp(this, 16), TvUi.dp(this, 14), TvUi.dp(this, 16), TvUi.dp(this, 14));
        TvUi.style(source, TvUi.HIGH, false);
        root.addView(source, TvUi.matchWrap());

        root.addView(TvUi.title(this, "播放线路", 22), TvUi.matchWrap());
        LinearLayout lines = new LinearLayout(this);
        root.addView(lines, TvUi.matchWrap());
        root.addView(TvUi.title(this, "选择剧集", 24), TvUi.matchWrap());
        GridLayout episodes = new GridLayout(this);
        episodes.setColumnCount(6);
        root.addView(episodes, TvUi.matchWrap());

        for (int index = 0; index < detail.lines.size(); index++) {
            PlayLine line = detail.lines.get(index);
            Button button = TvUi.button(this, line.name, index == 0);
            int selectedLine = index;
            button.setOnClickListener(view -> {
                lineIndex = selectedLine;
                selected = detail.lines.get(lineIndex).episodes.isEmpty() ? null : detail.lines.get(lineIndex).episodes.get(0);
                renderEpisodes(episodes);
            });
            lines.addView(button, new LinearLayout.LayoutParams(TvUi.dp(this, 180), TvUi.dp(this, 58)));
        }
        if (!detail.lines.isEmpty() && !detail.lines.get(0).episodes.isEmpty()) selected = detail.lines.get(0).episodes.get(0);
        renderEpisodes(episodes);
        play.setOnClickListener(view -> playEpisode(selected));
    }

    private void renderEpisodes(GridLayout grid) {
        grid.removeAllViews();
        if (detail.lines.isEmpty()) return;
        for (Episode episode : detail.lines.get(lineIndex).episodes) {
            Button button = TvUi.button(this, episode.name, episode == selected);
            button.setTag(episode);
            GridLayout.LayoutParams params = new GridLayout.LayoutParams();
            params.width = 0;
            params.height = TvUi.dp(this, 60);
            params.columnSpec = GridLayout.spec(GridLayout.UNDEFINED, 1f);
            params.setMargins(TvUi.dp(this, 5), TvUi.dp(this, 5), TvUi.dp(this, 5), TvUi.dp(this, 5));
            button.setLayoutParams(params);
            button.setOnClickListener(view -> {
                selected = episode;
                for (int index = 0; index < grid.getChildCount(); index++) {
                    Button child = (Button) grid.getChildAt(index);
                    TvUi.style(child, child.getTag() == episode ? TvUi.RED : TvUi.HIGH, child.hasFocus());
                }
                // TV interaction: choosing an episode starts playback immediately.
                playEpisode(episode);
            });
            grid.addView(button);
        }
    }

    private void playEpisode(Episode episode) {
        if (episode == null) return;
        selected = episode;
        String flag = detail.lines.isEmpty() ? "" : detail.lines.get(lineIndex).name;
        executor.execute(() -> {
            try {
                CmsCatalogueClient.ResolvedPlayback playback = TvServices.catalogue.resolvePlaybackResult(selectedSite, flag, episode.url);
                runOnUiThread(() -> {
                    library.record(item);
                    Intent intent = new Intent(this, playback.requiresParser ? WebParserActivity.class : TvPlayerActivity.class);
                    intent.putExtra("url", playback.url);
                    intent.putExtra("title", detail.item.name + " · " + episode.name);
                    intent.putExtra("mediaKey", item.siteKey + ":" + item.id);
                    startActivity(intent);
                });
            } catch (Exception error) {
                runOnUiThread(() -> Toast.makeText(this, "播放解析失败：" + message(error), Toast.LENGTH_LONG).show());
            }
        });
    }

    private static String message(Throwable value) { return value.getMessage() == null ? "未知错误" : value.getMessage(); }
    private static String safe(String value) { return value == null || value.trim().isEmpty() ? "暂无" : value; }
}
