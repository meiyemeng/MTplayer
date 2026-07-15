package cn.mtplayer.tv;
import android.app.Application;
public final class TvApplication extends Application { @Override public void onCreate(){super.onCreate();TvServices.initialize(this);} }
