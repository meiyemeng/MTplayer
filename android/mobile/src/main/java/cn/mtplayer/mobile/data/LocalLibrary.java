package cn.mtplayer.mobile.data;

import android.content.Context;
import android.content.SharedPreferences;
import com.google.gson.Gson;
import com.google.gson.reflect.TypeToken;
import java.lang.reflect.Type;
import java.util.ArrayList;
import java.util.List;
import cn.mtplayer.core.model.MediaItem;

public final class LocalLibrary {
    private static final Type LIST = new TypeToken<List<MediaItem>>(){}.getType();
    private static final Type STRING_LIST = new TypeToken<List<String>>(){}.getType();
    private final SharedPreferences prefs; private final Gson gson = new Gson();
    public LocalLibrary(Context c) { prefs = c.getSharedPreferences("mtplayer.library", Context.MODE_PRIVATE); }
    public List<MediaItem> favorites() { return read("favorites"); }
    public List<MediaItem> history() { return read("history"); }
    public boolean isFavorite(MediaItem item) { return favorites().stream().anyMatch(v -> same(v,item)); }
    public void toggleFavorite(MediaItem item) { List<MediaItem> list=favorites(); boolean removed=list.removeIf(v->same(v,item)); if(!removed) list.add(0,item); write("favorites",list); List<String> deleted=favoriteTombstones(); String key=key(item); if(removed){if(!deleted.contains(key))deleted.add(key);}else deleted.remove(key); prefs.edit().putString("favoriteTombstones",gson.toJson(deleted)).apply(); }
    public void record(MediaItem item) { List<MediaItem> list=history(); list.removeIf(v->same(v,item)); list.add(0,item); while(list.size()>100) list.remove(list.size()-1); write("history",list); }
    public void applySyncedFavorite(MediaItem item, boolean deleted) { List<MediaItem> list=favorites(); list.removeIf(v->same(v,item)); if(!deleted) list.add(0,item); write("favorites",list); }
    public void applySyncedHistory(MediaItem item, boolean deleted) { List<MediaItem> list=history(); list.removeIf(v->same(v,item)); if(!deleted) list.add(0,item); while(list.size()>100) list.remove(list.size()-1); write("history",list); }
    public List<String> favoriteTombstones() { List<String> v=gson.fromJson(prefs.getString("favoriteTombstones","[]"),STRING_LIST); return v==null?new ArrayList<>():new ArrayList<>(v); }
    public void clearFavoriteTombstones() { prefs.edit().remove("favoriteTombstones").apply(); }
    private List<MediaItem> read(String key) { List<MediaItem> v=gson.fromJson(prefs.getString(key,"[]"),LIST); return v==null?new ArrayList<>():new ArrayList<>(v); }
    private void write(String key,List<MediaItem> list) { prefs.edit().putString(key,gson.toJson(list)).apply(); }
    private static boolean same(MediaItem a,MediaItem b){return a!=null&&b!=null&&safe(a.siteKey).equals(safe(b.siteKey))&&safe(a.id).equals(safe(b.id));}
    private static String key(MediaItem item){return safe(item.siteKey)+"\n"+safe(item.id);}
    private static String safe(String v){return v==null?"":v;}
}
