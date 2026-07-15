package cn.mtplayer.core.config;

import java.io.Serializable;

public final class SourceGroup implements Serializable {
    public String id;
    public String name;
    public String url;
    public boolean enabled;
    public SourceGroup() { }
    public SourceGroup(String id, String name, String url, boolean enabled) {
        this.id = id; this.name = name; this.url = url; this.enabled = enabled;
    }
}
