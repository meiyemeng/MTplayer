package com.fongmi.android.tv.utils;

public class Github {

    public static final String URL = "https://raw.githubusercontent.com/meiyemeng/MTplayer/main";

    public static String getJson(String name) {
        return URL + "/updates/android/" + name + ".json";
    }

    public static String getApk(String name) {
        return "https://github.com/meiyemeng/MTplayer/releases/latest/download/MTPlayer-Android-" + name + ".apk";
    }
}
