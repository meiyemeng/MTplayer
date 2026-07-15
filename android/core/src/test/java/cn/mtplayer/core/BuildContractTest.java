package cn.mtplayer.core;

import static org.junit.Assert.assertEquals;
import org.junit.Test;

public class BuildContractTest {
    @Test public void api_floor_is_android_8() {
        assertEquals(26, BuildContract.MIN_SDK);
        assertEquals(35, BuildContract.TARGET_SDK);
    }
}
