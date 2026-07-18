package cn.mtplayer.core.model;

import java.io.Serializable;

public final class LiveChannel implements Serializable {
    public String group;
    public String name;
    public String url;
    public String logo;

    public LiveChannel() { }

    public LiveChannel(String group, String name, String url, String logo) {
        this.group = group;
        this.name = name;
        this.url = url;
        this.logo = logo;
    }
}
