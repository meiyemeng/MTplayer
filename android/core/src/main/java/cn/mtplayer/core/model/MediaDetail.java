package cn.mtplayer.core.model;

import java.io.Serializable;
import java.util.ArrayList;
import java.util.List;

public final class MediaDetail implements Serializable {
    public MediaItem item;
    public String content;
    public String actor;
    public String director;
    public final List<PlayLine> lines = new ArrayList<>();
}
