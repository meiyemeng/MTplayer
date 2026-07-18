package com.github.catvod.crawler;

import android.util.Log;

/**
 * Small host-side compatibility surface used by TVBox Spider plug-ins.
 * Plug-ins may reference this class even when debug logging is disabled.
 */
public final class SpiderDebug {
    private static final String DEFAULT_TAG = "MTPlayer-Spider";

    private SpiderDebug() { }

    public static boolean isEnabled() {
        return false;
    }

    public static void log(Throwable error) {
        if (error != null) Log.w(DEFAULT_TAG, error.getMessage(), error);
    }

    public static void log(String tag, Throwable error) {
        if (error != null) Log.w(safeTag(tag), error.getMessage(), error);
    }

    public static void log(String message) {
        Log.d(DEFAULT_TAG, message == null ? "" : message);
    }

    public static void log(String tag, String message, Object... args) {
        String rendered = message == null ? "" : message;
        if (args != null && args.length > 0) {
            try { rendered = String.format(rendered, args); }
            catch (RuntimeException ignored) { }
        }
        Log.d(safeTag(tag), rendered);
    }

    private static String safeTag(String value) {
        return value == null || value.trim().isEmpty() ? DEFAULT_TAG : value;
    }
}
