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
import cn.mtplayer.core.model.Site;
import cn.mtplayer.mobile.data.AppServices;
import cn.mtplayer.mobile.data.LocalLibrary;
import cn.mtplayer.mobile.ui.Ui;

@UnstableApi
public final class MainActivity extends AppCompatActivity {
    private final ExecutorService executor=Executors.newFixedThreadPool(4);
    private FrameLayout content;
    private LocalLibrary library;
    private List<Site> sites=new ArrayList<>();

    @Override protected void onCreate(Bundle state){super.onCreate(state);library=new LocalLibrary(this);setContentView(shell());showHome();}
    @Override protected void onDestroy(){executor.shutdownNow();super.onDestroy();}

    private View shell(){
        LinearLayout root=new LinearLayout(this);root.setOrientation(LinearLayout.VERTICAL);root.setBackgroundColor(Ui.BG);
        content=new FrameLayout(this);root.addView(content,new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT,0,1));
        HorizontalScrollView scroll=new HorizontalScrollView(this);scroll.setHorizontalScrollBarEnabled(false);scroll.setBackgroundColor(Ui.SURFACE);
        LinearLayout nav=new LinearLayout(this);nav.setGravity(Gravity.CENTER);String[] names={"首页","搜索","收藏","直播","账户","设置"};
        Runnable[] actions={this::showHome,this::showSearch,()->showLibrary(true),this::showLive,this::showAccount,this::showSettings};
        for(int i=0;i<names.length;i++){Button b=Ui.button(this,names[i],false);int index=i;b.setOnClickListener(v->actions[index].run());nav.addView(b,new LinearLayout.LayoutParams(Ui.dp(this,82),Ui.dp(this,58)));}
        scroll.addView(nav);root.addView(scroll,new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT,Ui.dp(this,58)));return root;
    }

    private void setPage(View view){content.removeAllViews();content.addView(view,new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT,ViewGroup.LayoutParams.MATCH_PARENT));}
    private LinearLayout page(String eyebrow,String title){LinearLayout root=Ui.column(this);TextView e=Ui.text(this,eyebrow);e.setTextColor(Ui.RED);root.addView(e,Ui.matchWrap());root.addView(Ui.title(this,title,30),Ui.matchWrap());return root;}
    private void showHome(){
        ScrollView scroll=new ScrollView(this);LinearLayout root=page("MT 精选","热门片单");
        ImageView logo=new ImageView(this);logo.setScaleType(ImageView.ScaleType.CENTER_INSIDE);Glide.with(this).load(R.drawable.logo_header).into(logo);root.addView(logo,new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT,Ui.dp(this,92)));
        Button source=Ui.button(this,"管理配置源",false);source.setOnClickListener(v->showSettings());root.addView(source,Ui.matchWrap());
        TextView status=Ui.text(this,"正在读取已启用配置源…");status.setPadding(0,Ui.dp(this,14),0,Ui.dp(this,8));root.addView(status,Ui.matchWrap());scroll.addView(root);setPage(scroll);
        executor.execute(()->{try{sites=AppServices.configurations.enabledSites();if(sites.isEmpty()){runOnUiThread(()->status.setText("还没有可用配置源，请点击上方按钮添加 HTTPS 配置地址。"));return;}List<MediaItem> latest=AppServices.catalogue.latest(sites,60);runOnUiThread(()->{status.setText("已启用 "+sites.size()+" 个接口");addTopRows(root,latest);});}catch(Exception ex){runOnUiThread(()->status.setText("配置读取失败："+message(ex)));}});
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
        LinearLayout root=page("直播频道","打开 M3U8 / 直播流");root.addView(Ui.text(this,"输入你拥有权限的直播流地址。完整 M3U 频道表可在后续设置中管理。"),Ui.matchWrap());EditText url=Ui.input(this,"https://.../live.m3u8");root.addView(url,new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT,Ui.dp(this,56)));Button play=Ui.button(this,"开始播放",true);root.addView(play,Ui.matchWrap());play.setOnClickListener(v->{String value=url.getText().toString().trim();if(!value.startsWith("https://")){toast("请输入 HTTPS 直播地址");return;}Intent i=new Intent(this,PlayerActivity.class);i.putExtra("url",value);i.putExtra("title","直播频道");startActivity(i);});setPage(root);
    }

    private void showAccount(){
        ScrollView scroll=new ScrollView(this);LinearLayout root=page("跨设备同步","账户与服务器");EditText server=Ui.input(this,"https://你的同步域名");server.setText(AppServices.account.serverUrl());EditText email=Ui.input(this,"邮箱");EditText password=Ui.input(this,"密码（至少 10 位）");password.setInputType(InputType.TYPE_CLASS_TEXT|InputType.TYPE_TEXT_VARIATION_PASSWORD);root.addView(server,new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT,Ui.dp(this,56)));root.addView(email,new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT,Ui.dp(this,56)));root.addView(password,new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT,Ui.dp(this,56)));TextView status=Ui.text(this,AppServices.account.signedIn()?"已登录；网络可用时可同步，本地播放始终可用。":"游客模式：可以本地播放，但不会同步。");root.addView(status,Ui.matchWrap());LinearLayout buttons=new LinearLayout(this);Button login=Ui.button(this,"登录",true),register=Ui.button(this,"注册",false),logout=Ui.button(this,"退出登录",false);buttons.addView(login,new LinearLayout.LayoutParams(0,Ui.dp(this,54),1));buttons.addView(register,new LinearLayout.LayoutParams(0,Ui.dp(this,54),1));root.addView(buttons,Ui.matchWrap());root.addView(logout,Ui.matchWrap());scroll.addView(root);setPage(scroll);
        login.setOnClickListener(v->accountAction(server,email,password,status,false));register.setOnClickListener(v->accountAction(server,email,password,status,true));logout.setOnClickListener(v->{AppServices.account.logout();status.setText("已退出登录。本地收藏、记录和配置没有删除。");});
    }

    private void accountAction(EditText server,EditText email,EditText password,TextView status,boolean register){
        try{AppServices.account.bind(server.getText().toString());}catch(Exception ex){status.setText(message(ex));return;}status.setText(register?"正在注册…":"正在登录…");executor.execute(()->{try{if(register)AppServices.account.register(email.getText().toString().trim(),password.getText().toString());else AppServices.account.login(email.getText().toString().trim(),password.getText().toString(),"android-mobile");runOnUiThread(()->status.setText(register?"注册请求已提交，请按服务器设置完成邮箱验证。":"登录成功，可以进行数据同步。"));}catch(Exception ex){runOnUiThread(()->status.setText((register?"注册失败：":"登录失败：")+message(ex)));}});
    }

    private void showSettings(){
        ScrollView scroll=new ScrollView(this);LinearLayout root=page("数据来源","配置源管理");root.addView(Ui.text(this,"可添加多个 TVBox 单仓或接口组；启用的配置会共同参与搜索。"),Ui.matchWrap());Button add=Ui.button(this,"＋ 添加配置源",true);root.addView(add,Ui.matchWrap());for(SourceGroup group:AppServices.configurations.groups()){LinearLayout row=new LinearLayout(this);row.setGravity(Gravity.CENTER_VERTICAL);Button toggle=Ui.button(this,(group.enabled?"✓ ":"○ ")+group.name,false);Button remove=Ui.button(this,"删除",false);toggle.setOnClickListener(v->{AppServices.configurations.setEnabled(group.id,!group.enabled);showSettings();});remove.setOnClickListener(v->{AppServices.configurations.remove(group.id);showSettings();});row.addView(toggle,new LinearLayout.LayoutParams(0,Ui.dp(this,56),1));row.addView(remove,new LinearLayout.LayoutParams(Ui.dp(this,82),Ui.dp(this,56)));root.addView(row,Ui.matchWrap());root.addView(Ui.text(this,group.url),Ui.matchWrap());}TextView about=Ui.text(this,"MT播放器 1.1.0\n软件不预置任何内容源，仅播放用户自行配置且有权访问的内容。用户应遵守所在地法律及内容授权规则。");about.setPadding(0,Ui.dp(this,28),0,Ui.dp(this,20));root.addView(about,Ui.matchWrap());add.setOnClickListener(v->sourceDialog());scroll.addView(root);setPage(scroll);
    }

    private void sourceDialog(){LinearLayout form=Ui.column(this);EditText name=Ui.input(this,"配置源名称");EditText url=Ui.input(this,"https://... 配置接口");form.addView(name,new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT,Ui.dp(this,56)));form.addView(url,new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT,Ui.dp(this,56)));AlertDialog dialog=new AlertDialog.Builder(this).setTitle("添加配置源").setView(form).setNegativeButton("取消",null).setPositiveButton("导入",null).create();dialog.setOnShowListener(v->dialog.getButton(AlertDialog.BUTTON_POSITIVE).setOnClickListener(x->{try{AppServices.configurations.add(name.getText().toString(),url.getText().toString());dialog.dismiss();showSettings();}catch(Exception ex){toast(message(ex));}}));dialog.show();}
    private void open(MediaItem item){Intent i=new Intent(this,DetailActivity.class);i.putExtra("item",item);startActivity(i);}
    private void toast(String value){Toast.makeText(this,value,Toast.LENGTH_LONG).show();}
    private static String message(Throwable ex){return ex.getMessage()==null?ex.getClass().getSimpleName():ex.getMessage();}
    private static String safe(String value){return value==null?"":value;}
}
