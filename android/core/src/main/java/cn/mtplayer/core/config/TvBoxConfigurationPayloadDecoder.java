package cn.mtplayer.core.config;

import android.util.Base64;

import java.nio.charset.StandardCharsets;

/** Decodes the Base64 envelope used by some TVBox configuration endpoints. */
final class TvBoxConfigurationPayloadDecoder {
    interface Decoder {
        byte[] decode(String value);
    }

    static String decode(String source) {
        return decode(source, value -> Base64.decode(value, Base64.DEFAULT));
    }

    static String decode(String source, Decoder decoder) {
        if (source == null || source.trim().isEmpty()) {
            throw new IllegalArgumentException("配置接口返回了空内容");
        }

        String trimmed = source.trim();
        if (!trimmed.startsWith("jhSP")) {
            return trimmed;
        }

        int separator = trimmed.indexOf("**");
        if (separator < 4 || separator + 2 >= trimmed.length()) {
            throw new IllegalArgumentException("配置接口返回的加密包装不完整");
        }

        try {
            byte[] decoded = decoder.decode(trimmed.substring(separator + 2));
            if (decoded == null) {
                throw new IllegalArgumentException("配置接口返回的 Base64 数据无效");
            }
            return new String(decoded, StandardCharsets.UTF_8).trim();
        } catch (IllegalArgumentException exception) {
            throw new IllegalArgumentException("配置接口返回的 Base64 数据无效", exception);
        }
    }

    private TvBoxConfigurationPayloadDecoder() { }
}
