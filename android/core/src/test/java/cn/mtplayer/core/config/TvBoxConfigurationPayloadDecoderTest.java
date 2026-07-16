package cn.mtplayer.core.config;

import static org.junit.Assert.assertEquals;

import java.nio.charset.StandardCharsets;
import java.util.Base64;
import org.junit.Test;

public final class TvBoxConfigurationPayloadDecoderTest {
    @Test
    public void decodesJhspBase64Envelope() {
        String json = "{\"sites\":[]}";
        String wrapped = "jhSPAyzn**" + Base64.getEncoder()
                .encodeToString(json.getBytes(StandardCharsets.UTF_8));

        String decoded = TvBoxConfigurationPayloadDecoder.decode(
                wrapped,
                value -> Base64.getDecoder().decode(value));

        assertEquals(json, decoded);
    }

    @Test
    public void leavesPlainJsonUntouched() {
        String json = "  {\"sites\":[]}  ";

        String decoded = TvBoxConfigurationPayloadDecoder.decode(
                json,
                value -> Base64.getDecoder().decode(value));

        assertEquals("{\"sites\":[]}", decoded);
    }
}
