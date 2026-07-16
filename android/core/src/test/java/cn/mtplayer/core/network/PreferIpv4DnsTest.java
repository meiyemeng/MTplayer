package cn.mtplayer.core.network;

import static org.junit.Assert.assertEquals;

import java.net.InetAddress;
import java.util.Arrays;
import java.util.List;
import org.junit.Test;

public final class PreferIpv4DnsTest {
    @Test
    public void usesOnlyIpv4WhenDualStackDnsHasBrokenIpv6Routes() throws Exception {
        InetAddress ipv6 = InetAddress.getByName("2001:db8::1");
        InetAddress ipv4 = InetAddress.getByName("192.0.2.1");

        List<InetAddress> ordered = PreferIpv4Dns.ipv4First(Arrays.asList(ipv6, ipv4));

        assertEquals(Arrays.asList(ipv4), ordered);
    }

    @Test
    public void preservesIpv6ForIpv6OnlyHosts() throws Exception {
        InetAddress ipv6 = InetAddress.getByName("2001:db8::1");

        assertEquals(Arrays.asList(ipv6), PreferIpv4Dns.ipv4First(Arrays.asList(ipv6)));
    }
}
