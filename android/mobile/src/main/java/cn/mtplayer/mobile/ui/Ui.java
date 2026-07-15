package cn.mtplayer.mobile.ui;

import android.content.Context;
import android.graphics.Color;
import android.graphics.Typeface;
import android.view.ViewGroup;
import android.widget.Button;
import android.widget.EditText;
import android.widget.LinearLayout;
import android.widget.TextView;

public final class Ui {
    public static final int BG = Color.rgb(9, 12, 16), SURFACE = Color.rgb(21, 25, 32), HIGH = Color.rgb(32, 37, 45);
    public static final int RED = Color.rgb(240, 25, 45), TEXT = Color.rgb(247, 248, 250), MUTED = Color.rgb(155, 163, 174);
    public static int dp(Context c, int value) { return Math.round(value * c.getResources().getDisplayMetrics().density); }
    public static TextView title(Context c, String text, int sp) { TextView v = new TextView(c); v.setText(text); v.setTextColor(TEXT); v.setTextSize(sp); v.setTypeface(Typeface.DEFAULT, Typeface.BOLD); return v; }
    public static TextView text(Context c, String text) { TextView v = new TextView(c); v.setText(text); v.setTextColor(MUTED); v.setTextSize(15); return v; }
    public static Button button(Context c, String text, boolean primary) { Button b = new Button(c); b.setText(text); b.setTextColor(TEXT); b.setTextSize(15); b.setAllCaps(false); b.setBackgroundTintList(android.content.res.ColorStateList.valueOf(primary ? RED : HIGH)); b.setMinHeight(dp(c, 48)); return b; }
    public static EditText input(Context c, String hint) { EditText e = new EditText(c); e.setHint(hint); e.setHintTextColor(MUTED); e.setTextColor(TEXT); e.setSingleLine(true); e.setTextSize(16); e.setBackgroundTintList(android.content.res.ColorStateList.valueOf(RED)); e.setPadding(dp(c, 14), 0, dp(c, 14), 0); return e; }
    public static LinearLayout column(Context c) { LinearLayout l = new LinearLayout(c); l.setOrientation(LinearLayout.VERTICAL); l.setPadding(dp(c, 18), dp(c, 16), dp(c, 18), dp(c, 20)); l.setBackgroundColor(BG); return l; }
    public static LinearLayout.LayoutParams matchWrap() { return new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT); }
    private Ui() { }
}
