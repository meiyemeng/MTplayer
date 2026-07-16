package cn.mtplayer.tv;

import android.content.Context;
import android.content.SharedPreferences;
import com.google.gson.Gson;
import com.google.gson.reflect.TypeToken;
import java.lang.reflect.Type;
import java.util.ArrayList;
import java.util.Iterator;
import java.util.List;
import cn.mtplayer.core.model.MediaItem;

public final class TvLibrary {
    private static final Type LIST = new TypeToken<List<MediaItem>>(){}.getType();
    private final SharedPreferences prefs;
    private final Gson gson = new Gson();
    public TvLibrary(Context context) { prefs = context.getSharedPreferences("mtplayer.library", Context.MODE_PRIVATE); }
    public List<MediaItem> favorites() { return read("favorites"); }
    public List<MediaItem> history() { return read("history"); }
    public boolean isFavorite(MediaItem item) { for (MediaItem value : favorites()) if (same(value, item)) return true; return false; }
    public void toggle(MediaItem item) { List<MediaItem> values=favorites(); boolean removed=remove(values,item); if(!removed)values.add(0,item); write("favorites",values); }
    public void record(MediaItem item) { List<MediaItem> values=history(); remove(values,item); values.add(0,item); while(values.size()>100)values.remove(values.size()-1); write("history",values); }
    private static boolean remove(List<MediaItem> values, MediaItem item) { boolean removed=false; for(Iterator<MediaItem> it=values.iterator();it.hasNext();)if(same(it.next(),item)){it.remove();removed=true;} return removed; }
    private List<MediaItem> read(String key) { List<MediaItem> values=gson.fromJson(prefs.getString(key,"[]"),LIST); return values==null?new ArrayList<>():new ArrayList<>(values); }
    private void write(String key,List<MediaItem> values) { prefs.edit().putString(key,gson.toJson(values)).apply(); }
    private static boolean same(MediaItem a,MediaItem b) { return a!=null&&b!=null&&safe(a.siteKey).equals(safe(b.siteKey))&&safe(a.id).equals(safe(b.id)); }
    private static String safe(String value) { return value==null?"":value; }
}
