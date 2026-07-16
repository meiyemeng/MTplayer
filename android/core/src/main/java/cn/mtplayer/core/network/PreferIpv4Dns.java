package cn.mtplayer.core.network;

import java.net.Inet4Address;
import java.net.InetAddress;
import java.net.UnknownHostException;
import java.util.ArrayList;
import java.util.List;
import okhttp3.Dns;

/**
 * Uses IPv4 whenever a host publishes an A record. IPv6 remains available
 * for IPv6-only hosts, but is not retried after a broken dual-stack route.
 */
public final class PreferIpv4Dns implements Dns {
    public static final PreferIpv4Dns INSTANCE = new PreferIpv4Dns();

    private PreferIpv4Dns() { }

    @Override
    public List<InetAddress> lookup(String hostname) throws UnknownHostException {
        return ipv4First(Dns.SYSTEM.lookup(hostname));
    }

    static List<InetAddress> ipv4First(List<InetAddress> addresses) {
        List<InetAddress> ipv4 = new ArrayList<>(addresses.size());
        for (InetAddress address : addresses) {
            if (address instanceof Inet4Address) {
                ipv4.add(address);
            }
        }
        return ipv4.isEmpty() ? new ArrayList<>(addresses) : ipv4;
    }
}
