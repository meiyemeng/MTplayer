package cn.mtplayer.core.sync;

import java.util.List;

import cn.mtplayer.core.model.MediaItem;

public interface SyncLibrary {
    List<MediaItem> favorites();
    List<MediaItem> history();
    List<String> favoriteTombstones();
    void clearFavoriteTombstones();
    void applySyncedFavorite(MediaItem item, boolean deleted);
    void applySyncedHistory(MediaItem item, boolean deleted);
}
