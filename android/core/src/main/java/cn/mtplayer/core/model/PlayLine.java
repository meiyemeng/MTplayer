package cn.mtplayer.core.model;

import java.io.Serializable;
import java.util.ArrayList;
import java.util.List;

public final class PlayLine implements Serializable {
    public String name;
    public final List<Episode> episodes = new ArrayList<>();
}
