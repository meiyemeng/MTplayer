package cn.mtplayer.core.model;

import java.io.Serializable;

public final class Episode implements Serializable {
    public String name;
    public String url;
    public Episode(String name, String url) { this.name = name; this.url = url; }
}
