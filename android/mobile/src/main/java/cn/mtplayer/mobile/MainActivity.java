package cn.mtplayer.mobile;

import android.app.AlertDialog;
import android.content.Intent;
import android.os.Bundle;
import android.text.InputType;
import android.view.Gravity;
import android.view.View;
import android.view.ViewGroup;
import android.widget.Button;
import android.widget.EditText;
import android.widget.FrameLayout;
import android.widget.HorizontalScrollView;
import android.widget.ImageView;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;
import android.widget.Toast;
import androidx.appcompat.app.AppCompatActivity;
import androidx.recyclerview.widget.GridLayoutManager;
import androidx.recyclerview.widget.LinearLayoutManager;
import androidx.recyclerview.widget.RecyclerView;
import androidx.media3.common.util.UnstableApi;
import com.bumptech.glide.Glide;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.List;
import java.util.Locale;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import cn.mtplayer.core.config.SourceGroup;
import cn.mtplayer.core.model.MediaItem;
import cn.mtplayer.core.model.LiveChannel;
import cn.mtplayer.core.model.Site;
import cn.mtplayer.mobile.data.AppServices;
import cn.mtplayer.core.sync.AndroidSyncService;
import cn.mtplayer.mobile.data.LocalLibrary;
import cn.mtplayer.mobile.ui.Ui;

@UnstableApi
public final class MainActivity extends AppCompatActivity {
    private final ExecutorService executor=Executors.newFixedThreadPool(4);
    private FrameLayout content;
    private LocalLibrary library;
    private AndroidSyncService sync;
    private List<Site> sites=new ArrayList<>();

    @Override protected void onCreate(Bundle state){super.onCreate(state);library=new LocalLibrary(this);sync=new AndroidSyncService(this,AppServices.account,AppServices.configurations,library);setContentView(shell());showHome();}
    @Override protected void onDestroy(){executor.shutdownNow();super.onDestroy();}

    private View shell(){
        boolean wide=getResources().getConfiguration().orientation==android.content.res.Configuration.ORIENTATION_LANDSCAPE||getResources().getConfiguration().smallestScreenWidthDp>=600;
        LinearLayout root=new LinearLayout(this);root.setOrientation(wide?LinearLayout.HORIZONTAL:LinearLayout.VERTICAL);root.setBackgroundColor(Ui.BG);
        String[] names={"首页","搜索","我的收藏","观看记录","直播频道","账户与同步","设置","关于软件"};
        Runnable[] actions={this::showHome,this::showSearch,()->showLibrary(true),()->showLibrary(false),this::showLive,this::showAccount,this::showSettings,this::showAbout};
        if(wide){LinearLayout rail=Ui.column(this);rail.setBackgroundColor(Ui.SURFACE);rail.setPadding(Ui.dp(this,18),Ui.dp(this,20),Ui.dp(this,18),Ui.dp(this,16));ImageView logo=new ImageView(this);logo.setScaleType(ImageView.ScaleType.CENTER_INSIDE);Glide.with(this).load(R.drawable.logo_header).into(logo);rail.addView(logo,new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT,Ui.dp(this,92)));for(int i=0;i<names.length;i++){Button b=Ui.button(this,names[i],false);int index=i;b.setOnClickListener(v->actions[index].run());rail.addView(b,new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT,Ui.dp(this,54)));}root.addView(rail,new LinearLayout.LayoutParams(Ui.dp(this,250),ViewGroup.LayoutParams.MATCH_PARENT));content=new FrameLayout(this);root.addView(content,new LinearLayout.LayoutParams(0,ViewGroup.LayoutParams.MATCH_PARENT,1));}
        else{content=new FrameLayout(this);root.addView(content,new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT,0,1));HorizontalScrollView scroll=new HorizontalScrollView(this);scroll.setHorizontalScrollBarEnabled(false);scroll.setBackgroundColor(Ui.SURFACE);LinearLayout nav=new LinearLayout(this);nav.setGravity(Gravity.CENTER);for(int i=0;i<names.length;i++){Button b=Ui.button(this,names[i],false);int index=i;b.setOnClickListener(v->actions[index].run());nav.addView(b,new LinearLayout.LayoutParams(Ui.dp(this,106),Ui.dp(this,58)));}scroll.addView(nav);root.addView(scroll,new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT,Ui.dp(this,58)));}
        return root;
    }

    private void setPage(View view){content.removeAllViews();content.addView(view,new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT,ViewGroup.LayoutParams.MATCH_PARENT));}
    private LinearLayout page(String eyebrow,String title){LinearLayout root=Ui.column(this);TextView e=Ui.text(this,eyebrow);e.setTextColor(Ui.RED);root.addView(e,Ui.matchWrap());root.addView(Ui.title(this,title,30),Ui.matchWrap());return root;}
    private void showHome(){
        ScrollView scroll=new ScrollView(this);LinearLayout root=page("MT 精选","热门片单");
        ImageView logo=new ImageView(this);logo.setScaleType(ImageView.ScaleType.CENTER_INSIDE);Glide.with(this).load(R.drawable.logo_header).into(logo);root.addView(logo,new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT,Ui.dp(this,92)));
        Button source=Ui.button(this,"管理配置源",false);source.setOnClickListener(v->showSettings());root.addView(source,Ui.matchWrap());
        TextView status=Ui.text(this,"正在读取已启用配置源…");status.setPadding(0,Ui.dp(this,14),0,Ui.dp(this,8));root.addView(status,Ui.matchWrap());scroll.addView(root);setPage(scroll);
        executor.execute(()->{try{sites=AppServices.configurations.enabledSites();if(sites.isEmpty()){runOnUiThread(()->status.setText("还没有可用配置源，请点击上方按钮添加 HTTP 或 HTTPS 配置地址。"));return;}List<MediaItem> latest=AppServices.catalogue.latest(sites,60);runOnUiThread(()->{status.setText("已启用 "+sites.size()+" 个接口");addTopRows(root,latest);});}catch(Exception ex){runOnUiThread(()->status.setText("配置读取失败："+message(ex)));}});
    }

    private void addTopRows(LinearLayout root,List<MediaItem> items){
        String[][] categories={{"电影 Top 10","电影"},{"电视剧 Top 10","电视|剧"},{"动漫电影 Top 10","动漫|动画"},{"动漫番剧 Top 10","番剧|动漫"},{"综艺 Top 10","综艺"}};
        for(String[] category:categories){List<MediaItem> filtered=new ArrayList<>();for(MediaItem item:items){String kind=(safe(item.type)+safe(item.name)).toLowerCase(Locale.ROOT);if(kind.matches(".*("+category[1]+").*"))filtered.add(item);if(filtered.size()==10)break;}if(filtered.isEmpty())filtered.addAll(items.subList(0,Math.min(10,items.size())));root.addView(Ui.title(this,category[0],22),Ui.matchWrap());RecyclerView row=new RecyclerView(this);row.setLayoutManager(new LinearLayoutManager(this,RecyclerView.HORIZONTAL,false));row.setAdapter(new MediaAdapter(filtered,true,this::open));root.addView(row,new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT,Ui.dp(this,255)));}
    }

    private void showSearch(){
        LinearLayout root=page("全接口聚合","搜索影片");LinearLayout bar=new LinearLayout(this);EditText input=Ui.input(this,"输入片名");Button search=Ui.button(this,"搜索",true);bar.addView(input,new LinearLayout.LayoutParams(0,Ui.dp(this,54),1));bar.addView(search,new LinearLayout.LayoutParams(Ui.dp(this,86),Ui.dp(this,54)));root.addView(bar,Ui.matchWrap());
        TextView status=Ui.text(this,"会并发查询所有已启用接口，只显示实际返回结果。");root.addView(status,Ui.matchWrap());RecyclerView list=new RecyclerView(this);list.setLayoutManager(new GridLayoutManager(this,getResources().getConfiguration().smallestScreenWidthDp>=600?5:2));root.addView(list,new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT,0,1));setPage(root);
        search.setOnClickListener(v->{String keyword=input.getText().toString().trim();if(keyword.isEmpty())return;status.setText("正在搜索多个接口…");search.setEnabled(false);executor.execute(()->{try{if(sites.isEmpty())sites=AppServices.configurations.enabledSites();List<MediaItem> result=AppServices.catalogue.searchAll(sites,keyword);runOnUiThread(()->{search.setEnabled(true);status.setText(result.isEmpty()?"没有找到影片，请检查配置源或更换关键词。":"找到 "+result.size()+" 个接口结果");list.setAdapter(new MediaAdapter(result,false,this::open));});}catch(Exception ex){runOnUiThread(()->{search.setEnabled(true);status.setText("搜索失败："+message(ex));});}});});
    }

    private void showLibrary(boolean favorites){
        LinearLayout root=page("本地保存",favorites?"我的收藏":"观看记录");LinearLayout switcher=new LinearLayout(this);Button fav=Ui.button(this,"我的收藏",favorites),history=Ui.button(this,"观看记录",!favorites);fav.setOnClickListener(v->showLibrary(true));history.setOnClickListener(v->showLibrary(false));switcher.addView(fav,new LinearLayout.LayoutParams(0,Ui.dp(this,50),1));switcher.addView(history,new LinearLayout.LayoutParams(0,Ui.dp(this,50),1));root.addView(switcher,Ui.matchWrap());List<MediaItem> values=favorites?library.favorites():library.history();TextView status=Ui.text(this,values.isEmpty()?"这里暂时没有内容。数据保存在本机，退出登录不会删除。":"共 "+values.size()+" 条本地记录");root.addView(status,Ui.matchWrap());RecyclerView list=new RecyclerView(this);list.setLayoutManager(new GridLayoutManager(this,2));list.setAdapter(new MediaAdapter(values,false,this::open));root.addView(list,new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT,0,1));setPage(root);
    }

    private void showLive(){
        ScrollView scroll=new ScrollView(this);LinearLayout root=page("LIVE & M3U8","直播频道");root.addView(Ui.text(this,"已启用配置中的直播源会自动识别；也可手动输入 HTTP/HTTPS 直播流。"),Ui.matchWrap());EditText url=Ui.input(this,"http:// 或 https://.../live.m3u8");root.addView(url,new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT,Ui.dp(this,56)));Button play=Ui.button(this,"添加并播放",true);root.addView(play,Ui.matchWrap());play.setOnClickListener(v->playLive(url.getText().toString().trim(),"直播频道"));TextView status=Ui.text(this,"正在读取配置中的直播频道…");root.addView(status,Ui.matchWrap());LinearLayout channels=Ui.column(this);root.addView(channels,Ui.matchWrap());scroll.addView(root);setPage(scroll);
        executor.execute(()->{try{List<LiveChannel> values=AppServices.configurations.enabledLiveChannels();runOnUiThread(()->{status.setText(values.isEmpty()?"配置中没有读取到可用直播频道。":"共识别 "+values.size()+" 个直播频道，点击即可播放。");for(LiveChannel channel:values){Button button=Ui.button(this,(safe(channel.group).isEmpty()?"":channel.group+" · ")+channel.name,false);button.setOnClickListener(v->playLive(channel.url,channel.name));channels.addView(button,Ui.matchWrap());}});}catch(Exception ex){runOnUiThread(()->status.setText("直播源读取失败："+message(ex)));}});
    }

    private void playLive(String value,String title){if(!isHttpAddress(value)){toast("请输入有效的 HTTP 或 HTTPS 直播地址");return;}Intent i=new Intent(this,PlayerActivity.class);i.putExtra("url",value);i.putExtra("title",title);startActivity(i);}

    private void showAccount(){
        ScrollView scroll=new ScrollView(this);LinearLayout root=page("注册 · 登录 · 同步","账户与服务器");
        root.addView(Ui.text(this,"配置源和视频地址支持 HTTP/HTTPS；为保护密码和令牌，同步服务器必须使用 HTTPS 域名。"),Ui.matchWrap());
        EditText server=Ui.input(this,"https://你的同步域名");server.setText(AppServices.account.serverUrl());
        EditText email=Ui.input(this,"邮箱");email.setText(AppServices.account.email());
        EditText password=Ui.input(this,"密码（至少 10 位）");password.setInputType(InputType.TYPE_CLASS_TEXT|InputType.TYPE_TEXT_VARIATION_PASSWORD);
        root.addView(server,new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT,Ui.dp(this,56)));root.addView(email,new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT,Ui.dp(this,56)));root.addView(password,new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT,Ui.dp(this,56)));
        TextView status=Ui.text(this,AppServices.account.signedIn()?"已登录，可同步收藏、观看记录和配置源。":"游客模式：本地播放可用，但不会同步。");root.addView(status,Ui.matchWrap());
        LinearLayout buttons=new LinearLayout(this);Button login=Ui.button(this,"登录",true),register=Ui.button(this,"注册新账户",false),syncNow=Ui.button(this,"双向同步",true),upload=Ui.button(this,"上传本地数据",false),download=Ui.button(this,"下载云端数据",false),logout=Ui.button(this,"退出登录",false);
        buttons.addView(login,new LinearLayout.LayoutParams(0,Ui.dp(this,54),1));buttons.addView(register,new LinearLayout.LayoutParams(0,Ui.dp(this,54),1));root.addView(buttons,Ui.matchWrap());LinearLayout directions=new LinearLayout(this);directions.addView(upload,new LinearLayout.LayoutParams(0,Ui.dp(this,54),1));directions.addView(download,new LinearLayout.LayoutParams(0,Ui.dp(this,54),1));root.addView(syncNow,Ui.matchWrap());root.addView(directions,Ui.matchWrap());root.addView(logout,Ui.matchWrap());scroll.addView(root);setPage(scroll);
        login.setOnClickListener(v->accountAction(server,email,password,status,false));register.setOnClickListener(v->accountAction(server,email,password,status,true));syncNow.setOnClickListener(v->runSync(status,0));upload.setOnClickListener(v->runSync(status,1));download.setOnClickListener(v->runSync(status,2));logout.setOnClickListener(v->{AppServices.account.logout();status.setText("已退出登录。本地收藏、记录和配置没有删除。");});
    }

    private void accountAction(EditText server,EditText email,EditText password,TextView status,boolean register){
        try{AppServices.account.bind(server.getText().toString());}catch(Exception ex){status.setText(message(ex));return;}status.setText(register?"正在注册…":"正在登录…");executor.execute(()->{try{if(register){AppServices.account.register(email.getText().toString().trim(),password.getText().toString());runOnUiThread(()->status.setText("注册请求已提交，请按服务器设置完成邮箱验证后登录。"));}else{AppServices.account.login(email.getText().toString().trim(),password.getText().toString(),"android-mobile");runOnUiThread(()->status.setText("登录成功，可选择上传、下载或双向同步。"));}}catch(Exception ex){runOnUiThread(()->status.setText((register?"注册失败：":"登录失败：")+message(ex)));}});
    }

    private void runSync(TextView status,int mode){if(!AppServices.account.signedIn()){status.setText("请先登录再同步。");return;}status.setText(mode==1?"正在上传本地数据…":mode==2?"正在下载云端数据…":"正在双向同步…");executor.execute(()->runSyncInBackground(status,mode));}
    private void runSyncInBackground(TextView status,int mode){try{AndroidSyncService.Result result=mode==1?sync.upload():mode==2?sync.download():sync.synchronize();runOnUiThread(()->status.setText("同步完成：上传 "+result.pushed+" 项，下载 "+result.pulled+" 项。"));}catch(Exception ex){runOnUiThread(()->status.setText("同步失败："+message(ex)));}}

    private void showSettings(){
        ScrollView scroll=new ScrollView(this);LinearLayout root=page("数据来源","配置源管理");
        root.addView(Ui.text(this,"支持 HTTP/HTTPS，可添加多个 TVBox 单仓或接口组；启用的配置会共同参与搜索。"),Ui.matchWrap());
        Button add=Ui.button(this,"＋ 添加配置源",true);root.addView(add,Ui.matchWrap());
        for(SourceGroup group:AppServices.configurations.groups()){
            LinearLayout row=new LinearLayout(this);row.setGravity(Gravity.CENTER_VERTICAL);
            Button toggle=Ui.button(this,(group.enabled?"✓ ":"○ ")+group.name,false);Button remove=Ui.button(this,"删除",false);
            toggle.setOnClickListener(v->{AppServices.configurations.setEnabled(group.id,!group.enabled);showSettings();});
            remove.setOnClickListener(v->{AppServices.configurations.remove(group.id);showSettings();});
            row.addView(toggle,new LinearLayout.LayoutParams(0,Ui.dp(this,56),1));row.addView(remove,new LinearLayout.LayoutParams(Ui.dp(this,82),Ui.dp(this,56)));
            root.addView(row,Ui.matchWrap());root.addView(Ui.text(this,group.url),Ui.matchWrap());
        }
        root.addView(Ui.title(this,"Spider Gateway",20),Ui.matchWrap());
        Button gateway=Ui.button(this,AppServices.spiderGateway.isEnabled()?"关闭局域网 Gateway":"启用局域网 Gateway",AppServices.spiderGateway.isEnabled());
        root.addView(gateway,Ui.matchWrap());
        root.addView(Ui.text(this,"供 Windows/Web 调用 Android CSP 插件。端口："+AppServices.spiderGateway.port()+"\n令牌："+AppServices.spiderGateway.token()+"\n仅在可信局域网使用。"),Ui.matchWrap());
        gateway.setOnClickListener(v->{try{AppServices.spiderGateway.setEnabled(!AppServices.spiderGateway.isEnabled());showSettings();}catch(Exception ex){toast(message(ex));}});
        TextView about=Ui.text(this,"MT播放器 1.3.1\n软件不预置任何内容源，仅播放用户自行配置且有权访问的内容。用户应遵守所在地法律及内容授权规则。\n\n源码与下载：https://github.com/meiyemeng/MTplayer/releases/latest");
        about.setPadding(0,Ui.dp(this,28),0,Ui.dp(this,20));root.addView(about,Ui.matchWrap());
        add.setOnClickListener(v->sourceDialog());scroll.addView(root);setPage(scroll);
    }

    private void showAbout(){
        ScrollView scroll=new ScrollView(this);LinearLayout root=page("ABOUT MT PLAYER","关于软件");
        ImageView logo=new ImageView(this);logo.setScaleType(ImageView.ScaleType.CENTER_INSIDE);Glide.with(this).load(R.drawable.logo_header).into(logo);root.addView(logo,new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT,Ui.dp(this,100)));
        root.addView(Ui.title(this,"MT播放器 Android · 1.3.1",22),Ui.matchWrap());
        root.addView(Ui.text(this,"源码仓库：https://github.com/meiyemeng/MTplayer\n客户端下载：https://github.com/meiyemeng/MTplayer/releases/latest\n\n软件不预置、不存储、不上传、不分发任何影视内容，仅播放用户自行配置且有权访问的媒体。"),Ui.matchWrap());
        root.addView(Ui.title(this,"支持项目",20),Ui.matchWrap());
        ImageView donate=new ImageView(this);donate.setScaleType(ImageView.ScaleType.CENTER_INSIDE);Glide.with(this).load(R.drawable.alipay_donate).into(donate);root.addView(donate,new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT,Ui.dp(this,430)));
        root.addView(Ui.text(this,"支付宝扫码为自愿捐助，不解锁内容或会员权益，也不代表购买影视服务。"),Ui.matchWrap());
        scroll.addView(root);setPage(scroll);
    }

    private void sourceDialog(){LinearLayout form=Ui.column(this);EditText name=Ui.input(this,"配置源名称");EditText url=Ui.input(this,"http:// 或 https:// 配置接口");form.addView(name,new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT,Ui.dp(this,56)));form.addView(url,new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT,Ui.dp(this,56)));AlertDialog dialog=new AlertDialog.Builder(this).setTitle("添加配置源").setView(form).setNegativeButton("取消",null).setPositiveButton("导入",null).create();dialog.setOnShowListener(v->dialog.getButton(AlertDialog.BUTTON_POSITIVE).setOnClickListener(x->{try{AppServices.configurations.add(name.getText().toString(),url.getText().toString());dialog.dismiss();showSettings();}catch(Exception ex){toast(message(ex));}}));dialog.show();}
    private void open(MediaItem item){Intent i=new Intent(this,DetailActivity.class);i.putExtra("item",item);startActivity(i);}
    private void toast(String value){Toast.makeText(this,value,Toast.LENGTH_LONG).show();}
    private static String message(Throwable ex){return ex.getMessage()==null?ex.getClass().getSimpleName():ex.getMessage();}
    private static String safe(String value){return value==null?"":value;}
    private static boolean isHttpAddress(String value){try{java.net.URI uri=java.net.URI.create(value);return uri.getHost()!=null&&("http".equalsIgnoreCase(uri.getScheme())||"https".equalsIgnoreCase(uri.getScheme()));}catch(RuntimeException ex){return false;}}
}
