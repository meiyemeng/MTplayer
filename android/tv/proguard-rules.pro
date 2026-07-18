-keep class cn.mtplayer.core.** { *; }
-keep class cn.mtplayer.tv.** { *; }
-keep class com.github.catvod.crawler.** { *; }
# Keep the binary ABI consumed by dynamically loaded TVBox Spider JARs.
-keep class com.google.gson.** { *; }
-keep class okhttp3.** { *; }
-keep class okio.** { *; }
-dontwarn org.conscrypt.**
