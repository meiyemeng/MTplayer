package cn.mtplayer.mobile;

import android.content.Context;
import android.content.SharedPreferences;
import android.content.pm.ActivityInfo;
import android.media.AudioManager;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.view.Gravity;
import android.view.View;
import android.view.ViewGroup;
import android.view.Window;
import android.view.WindowInsets;
import android.widget.Button;
import android.widget.LinearLayout;
import android.widget.TextView;
import android.widget.Toast;
import androidx.appcompat.app.AppCompatActivity;
import androidx.media3.common.MediaItem;
import androidx.media3.common.Player;
import androidx.media3.common.util.UnstableApi;
import androidx.media3.exoplayer.ExoPlayer;
import androidx.media3.ui.PlayerView;
import cn.mtplayer.mobile.ui.Ui;

@UnstableApi
public final class PlayerActivity extends AppCompatActivity {
    private ExoPlayer player; private PlayerView playerView; private final Handler timer=new Handler(Looper.getMainLooper()); private SharedPreferences prefs; private String key; private boolean introApplied=false,outroApplied=false; private LinearLayout overlay;
    private final Runnable tick=new Runnable(){public void run(){if(player!=null&&player.isPlaying()){long pos=player.getCurrentPosition(),duration=player.getDuration();int intro=prefs.getInt(key+".intro",0),outro=prefs.getInt(key+".outro",0);if(!introApplied&&intro>0&&pos<intro*1000L){introApplied=true;player.seekTo(intro*1000L);}if(!outroApplied&&outro>0&&duration>0&&duration-pos<=outro*1000L){outroApplied=true;player.pause();toast("已到达片尾跳过点");}prefs.edit().putLong(key+".position",pos).apply();}timer.postDelayed(this,1000);}};
    @Override protected void onCreate(Bundle state){super.onCreate(state);requestWindowFeature(Window.FEATURE_NO_TITLE);getWindow().setStatusBarColor(android.graphics.Color.BLACK);getWindow().setNavigationBarColor(android.graphics.Color.BLACK);getWindow().getDecorView().setSystemUiVisibility(View.SYSTEM_UI_FLAG_FULLSCREEN|View.SYSTEM_UI_FLAG_IMMERSIVE_STICKY|View.SYSTEM_UI_FLAG_HIDE_NAVIGATION);prefs=getSharedPreferences("mtplayer.playback",Context.MODE_PRIVATE);String url=getIntent().getStringExtra("url"),title=getIntent().getStringExtra("title");key=getIntent().getStringExtra("mediaKey");if(key==null)key="stream:"+url.hashCode();setContentView(buildView(title));player=new ExoPlayer.Builder(this).build();playerView.setPlayer(player);player.setMediaItem(MediaItem.fromUri(url));player.prepare();long resume=prefs.getLong(key+".position",0);if(resume>0)player.seekTo(resume);player.play();timer.post(tick);}
    private View buildView(String title){android.widget.FrameLayout root=new android.widget.FrameLayout(this);root.setBackgroundColor(android.graphics.Color.BLACK);playerView=new PlayerView(this);playerView.setUseController(true);playerView.setControllerShowTimeoutMs(5000);root.addView(playerView,new android.widget.FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT,ViewGroup.LayoutParams.MATCH_PARENT));overlay=new LinearLayout(this);overlay.setOrientation(LinearLayout.VERTICAL);overlay.setPadding(Ui.dp(this,20),Ui.dp(this,14),Ui.dp(this,20),Ui.dp(this,14));overlay.setBackgroundColor(0xAA090C10);TextView heading=Ui.title(this,title==null?"MT播放器":title,20);overlay.addView(heading,Ui.matchWrap());LinearLayout actions=new LinearLayout(this);actions.setGravity(Gravity.CENTER);String[] labels={"设片头","设片尾","清除跳过","静音","1.0×","全屏"};for(String label:labels){Button b=Ui.button(this,label,false);actions.addView(b,new LinearLayout.LayoutParams(0,Ui.dp(this,50),1));switch(label){case"设片头":b.setOnClickListener(v->{prefs.edit().putInt(key+".intro",(int)(player.getCurrentPosition()/1000)).apply();toast("已将当前时间设为片头结束");});break;case"设片尾":b.setOnClickListener(v->{long remain=Math.max(0,player.getDuration()-player.getCurrentPosition());prefs.edit().putInt(key+".outro",(int)(remain/1000)).apply();toast("已将当前时间设为片尾开始");});break;case"清除跳过":b.setOnClickListener(v->{prefs.edit().remove(key+".intro").remove(key+".outro").apply();toast("已清除此影片的片头片尾设置");});break;case"静音":b.setOnClickListener(v->{player.setVolume(player.getVolume()>0?0:1);b.setText(player.getVolume()>0?"静音":"取消静音");});break;case"1.0×":b.setOnClickListener(v->{float next=player.getPlaybackParameters().speed<1.25f?1.25f:player.getPlaybackParameters().speed<1.5f?1.5f:player.getPlaybackParameters().speed<2f?2f:1f;player.setPlaybackSpeed(next);b.setText(next+"×");});break;case"全屏":b.setOnClickListener(v->setRequestedOrientation(getResources().getConfiguration().orientation==android.content.res.Configuration.ORIENTATION_LANDSCAPE?ActivityInfo.SCREEN_ORIENTATION_SENSOR_PORTRAIT:ActivityInfo.SCREEN_ORIENTATION_SENSOR_LANDSCAPE));break;}}overlay.addView(actions,Ui.matchWrap());android.widget.FrameLayout.LayoutParams p=new android.widget.FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT,ViewGroup.LayoutParams.WRAP_CONTENT,Gravity.TOP);root.addView(overlay,p);root.setOnClickListener(v->{overlay.setVisibility(View.VISIBLE);timer.removeCallbacks(hide);timer.postDelayed(hide,5000);playerView.showController();});timer.postDelayed(hide,5000);return root;}
    private final Runnable hide=()->overlay.setVisibility(View.GONE);
    private void toast(String v){Toast.makeText(this,v,Toast.LENGTH_SHORT).show();}
    @Override protected void onPause(){if(player!=null){prefs.edit().putLong(key+".position",player.getCurrentPosition()).apply();player.pause();}super.onPause();}
    @Override protected void onDestroy(){timer.removeCallbacksAndMessages(null);if(player!=null)player.release();super.onDestroy();}
}
