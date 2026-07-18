-keep class cn.mtplayer.core.** { *; }
-keep class cn.mtplayer.mobile.** { *; }
-keep class com.github.catvod.crawler.** { *; }
# TVBox Spider JARs are loaded after R8 has finished. Their bytecode links to
# these host libraries by the original JVM class/method names, so shrinking or
# renaming them makes every csp_* site fail only in release builds.
-keep class com.google.gson.** { *; }
-keep class okhttp3.** { *; }
-keep class okio.** { *; }
-dontwarn org.conscrypt.**
