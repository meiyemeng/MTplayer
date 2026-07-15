package cn.mtplayer.mobile;

import android.app.Application;
import cn.mtplayer.mobile.data.AppServices;

public final class MobileApplication extends Application {
    @Override public void onCreate() { super.onCreate(); AppServices.initialize(this); }
}
