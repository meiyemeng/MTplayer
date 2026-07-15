package cn.mtplayer.core.model;

import java.io.Serializable;

public final class Site implements Serializable {
    public String key;
    public String name;
    public String api;
    public boolean enabled = true;

    public Site() { }
    public Site(String key, String name, String api) {
        this.key = key;
        this.name = name;
        this.api = api;
    }
}
