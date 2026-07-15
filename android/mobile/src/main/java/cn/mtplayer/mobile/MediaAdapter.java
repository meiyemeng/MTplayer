package cn.mtplayer.mobile;

import android.view.View;
import android.view.ViewGroup;
import android.widget.ImageView;
import android.widget.LinearLayout;
import android.widget.TextView;
import androidx.annotation.NonNull;
import androidx.recyclerview.widget.RecyclerView;
import com.bumptech.glide.Glide;
import java.util.List;
import cn.mtplayer.core.model.MediaItem;
import cn.mtplayer.mobile.ui.Ui;

public final class MediaAdapter extends RecyclerView.Adapter<MediaAdapter.Holder> {
    public interface Listener { void open(MediaItem item); }
    private final List<MediaItem> items; private final Listener listener; private final boolean horizontal;
    public MediaAdapter(List<MediaItem> items, boolean horizontal, Listener listener) { this.items=items; this.listener=listener; this.horizontal=horizontal; }
    @NonNull @Override public Holder onCreateViewHolder(@NonNull ViewGroup parent,int type){
        LinearLayout card=new LinearLayout(parent.getContext()); card.setOrientation(LinearLayout.VERTICAL); card.setPadding(Ui.dp(parent.getContext(),5),Ui.dp(parent.getContext(),5),Ui.dp(parent.getContext(),5),Ui.dp(parent.getContext(),10)); card.setBackgroundColor(Ui.BG);
        int width=Ui.dp(parent.getContext(),horizontal?132:155); card.setLayoutParams(new ViewGroup.LayoutParams(width,ViewGroup.LayoutParams.WRAP_CONTENT));
        ImageView poster=new ImageView(parent.getContext()); poster.setScaleType(ImageView.ScaleType.CENTER_CROP); poster.setBackgroundColor(Ui.HIGH); card.addView(poster,new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT,Ui.dp(parent.getContext(),horizontal?190:220)));
        TextView title=Ui.title(parent.getContext(),"",15); title.setMaxLines(2); card.addView(title,Ui.matchWrap());
        TextView meta=Ui.text(parent.getContext(),""); meta.setTextSize(12); card.addView(meta,Ui.matchWrap()); return new Holder(card,poster,title,meta);
    }
    @Override public void onBindViewHolder(@NonNull Holder h,int pos){MediaItem item=items.get(pos);h.title.setText(item.name);h.meta.setText(item.siteName+"  "+item.remarks);Glide.with(h.poster).load(item.poster).centerCrop().placeholder(android.R.color.darker_gray).into(h.poster);h.itemView.setOnClickListener(v->listener.open(item));}
    @Override public int getItemCount(){return items.size();}
    static final class Holder extends RecyclerView.ViewHolder {final ImageView poster;final TextView title,meta;Holder(View v,ImageView p,TextView t,TextView m){super(v);poster=p;title=t;meta=m;}}
}
