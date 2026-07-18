package cn.mtplayer.mobile;

import android.content.Intent;
import android.os.Bundle;
import android.view.ViewGroup;
import android.widget.Button;
import android.widget.GridLayout;
import android.widget.ImageView;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.Spinner;
import android.widget.ArrayAdapter;
import android.widget.TextView;
import androidx.appcompat.app.AppCompatActivity;
import androidx.media3.common.util.UnstableApi;
import com.bumptech.glide.Glide;
import java.util.ArrayList;
import java.util.List;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import cn.mtplayer.core.model.Episode;
import cn.mtplayer.core.model.MediaDetail;
import cn.mtplayer.core.model.MediaItem;
import cn.mtplayer.core.model.PlayLine;
import cn.mtplayer.core.model.Site;
import cn.mtplayer.core.catalogue.CmsCatalogueClient;
import cn.mtplayer.core.player.WebParserActivity;
import cn.mtplayer.mobile.data.AppServices;
import cn.mtplayer.mobile.data.LocalLibrary;
import cn.mtplayer.mobile.ui.Ui;

@UnstableApi
public final class DetailActivity extends AppCompatActivity {
    private final ExecutorService executor=Executors.newSingleThreadExecutor();
    private MediaItem item; private MediaDetail detail; private LinearLayout root; private LocalLibrary library; private Site selectedSite;
    @Override protected void onCreate(Bundle state){super.onCreate(state);item=(MediaItem)getIntent().getSerializableExtra("item");library=new LocalLibrary(this);ScrollView scroll=new ScrollView(this);root=Ui.column(this);scroll.addView(root);setContentView(scroll);renderLoading();load();}
    @Override protected void onDestroy(){executor.shutdownNow();super.onDestroy();}
    private void renderLoading(){root.removeAllViews();root.addView(Ui.text(this,"‹ 返回"),Ui.matchWrap());root.addView(Ui.title(this,item==null?"影片详情":item.name,30),Ui.matchWrap());root.addView(Ui.text(this,"正在检查所选接口并加载影片信息…"),Ui.matchWrap());}
    private void load(){executor.execute(()->{try{List<Site> sites=AppServices.configurations.enabledSites();for(Site site:sites)if(site.key.equals(item.siteKey)){selectedSite=site;break;}if(selectedSite==null)throw new IllegalStateException("该播放接口当前未启用");detail=AppServices.catalogue.detail(selectedSite,item.id);runOnUiThread(this::render);}catch(Exception ex){runOnUiThread(()->{root.addView(Ui.text(this,"加载失败："+(ex.getMessage()==null?"未知错误":ex.getMessage())),Ui.matchWrap());});}});}
    private void render(){
        root.removeAllViews();ImageView poster=new ImageView(this);poster.setScaleType(ImageView.ScaleType.CENTER_CROP);Glide.with(this).load(detail.item.poster).into(poster);root.addView(poster,new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT,Ui.dp(this,390)));
        TextView type=Ui.text(this,detail.item.type+" · "+detail.item.year+" · "+detail.item.siteName);type.setTextColor(Ui.RED);root.addView(type,Ui.matchWrap());root.addView(Ui.title(this,detail.item.name,32),Ui.matchWrap());root.addView(Ui.text(this,"导演："+safe(detail.director)+"\n主演："+safe(detail.actor)+"\n\n"+safe(detail.content)),Ui.matchWrap());
        LinearLayout actions=new LinearLayout(this);Button play=Ui.button(this,"▶ 播放所选集",true),favorite=Ui.button(this,library.isFavorite(item)?"♥ 已收藏":"♡ 加入收藏",false);actions.addView(play,new LinearLayout.LayoutParams(0,Ui.dp(this,56),1));actions.addView(favorite,new LinearLayout.LayoutParams(0,Ui.dp(this,56),1));root.addView(actions,Ui.matchWrap());favorite.setOnClickListener(v->{library.toggleFavorite(item);favorite.setText(library.isFavorite(item)?"♥ 已收藏":"♡ 加入收藏");});
        root.addView(Ui.title(this,"播放接口",18),Ui.matchWrap());TextView source=Ui.text(this,detail.item.siteName+"（已确认包含该影片）");source.setTextColor(Ui.TEXT);source.setBackgroundColor(Ui.HIGH);source.setPadding(Ui.dp(this,14),Ui.dp(this,14),Ui.dp(this,14),Ui.dp(this,14));root.addView(source,Ui.matchWrap());
        root.addView(Ui.title(this,"播放线路",18),Ui.matchWrap());Spinner lines=new Spinner(this);List<String> names=new ArrayList<>();for(PlayLine line:detail.lines)names.add(line.name);lines.setAdapter(new ArrayAdapter<>(this,android.R.layout.simple_spinner_dropdown_item,names));root.addView(lines,new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT,Ui.dp(this,56)));
        root.addView(Ui.title(this,"选择剧集",22),Ui.matchWrap());GridLayout episodes=new GridLayout(this);episodes.setColumnCount(3);root.addView(episodes,new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT,ViewGroup.LayoutParams.WRAP_CONTENT));final Episode[] selected={detail.lines.isEmpty()||detail.lines.get(0).episodes.isEmpty()?null:detail.lines.get(0).episodes.get(0)};
        Runnable refresh=()->{episodes.removeAllViews();if(detail.lines.isEmpty())return;PlayLine line=detail.lines.get(lines.getSelectedItemPosition());for(Episode episode:line.episodes){Button b=Ui.button(this,episode.name,episode==selected[0]);GridLayout.LayoutParams p=new GridLayout.LayoutParams();p.width=0;p.height=Ui.dp(this,54);p.columnSpec=GridLayout.spec(GridLayout.UNDEFINED,1f);p.setMargins(Ui.dp(this,3),Ui.dp(this,3),Ui.dp(this,3),Ui.dp(this,3));b.setLayoutParams(p);b.setOnClickListener(v->{selected[0]=episode;refreshEpisodeStyles(episodes,episode);});b.setTag(episode);episodes.addView(b);}};
        lines.setOnItemSelectedListener(new android.widget.AdapterView.OnItemSelectedListener(){public void onNothingSelected(android.widget.AdapterView<?> p){}public void onItemSelected(android.widget.AdapterView<?> p,android.view.View v,int pos,long id){selected[0]=detail.lines.get(pos).episodes.isEmpty()?null:detail.lines.get(pos).episodes.get(0);refresh.run();}});refresh.run();
        play.setOnClickListener(v->{if(selected[0]!=null)launch(selected[0],detail.lines.get(lines.getSelectedItemPosition()).name);});
    }
    private void launch(Episode episode,String flag){executor.execute(()->{try{CmsCatalogueClient.ResolvedPlayback playback=AppServices.catalogue.resolvePlaybackResult(selectedSite,flag,episode.url);runOnUiThread(()->{library.record(item);Intent i=new Intent(this,playback.requiresParser?WebParserActivity.class:PlayerActivity.class);i.putExtra("url",playback.url);i.putExtra("title",detail.item.name+" · "+episode.name);i.putExtra("mediaKey",item.siteKey+":"+item.id);startActivity(i);});}catch(Exception ex){runOnUiThread(()->android.widget.Toast.makeText(this,"播放解析失败："+(ex.getMessage()==null?"未知错误":ex.getMessage()),android.widget.Toast.LENGTH_LONG).show());}});}
    private void refreshEpisodeStyles(GridLayout grid,Episode selected){for(int i=0;i<grid.getChildCount();i++){Button b=(Button)grid.getChildAt(i);b.setBackgroundTintList(android.content.res.ColorStateList.valueOf(b.getTag()==selected?Ui.RED:Ui.HIGH));}}
    private static String safe(String value){return value==null||value.isBlank()?"暂无":value;}
}
