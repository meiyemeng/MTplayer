package cn.mtplayer.core.model;

import java.io.Serializable;

public final class Site implements Serializable {
    public String key;
    public String name;
    public String api;
    public int type;
    public String jar;
    public String ext;
    public boolean searchable = true;
    public boolean enabled = true;

    public Site() { }
    public Site(String key, String name, String api) {
        this(key, name, api, 1, null, null, true);
    }

    public Site(String key, String name, String api, int type, String jar, String ext, boolean searchable) {
        this.key = key;
        this.name = name;
        this.api = api;
        this.type = type;
        this.jar = jar;
        this.ext = ext;
        this.searchable = searchable;
    }

    public boolean isCsp() { return type == 3 && api != null && api.startsWith("csp_"); }
}
