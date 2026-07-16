package cn.mtplayer.core.network;

import java.net.Inet4Address;
import java.net.InetAddress;
import java.net.UnknownHostException;
import java.util.ArrayList;
import java.util.List;
import okhttp3.Dns;

/**
 * Keeps every DNS result, but tries IPv4 first on Android devices whose
 * network advertises IPv6 without providing a working IPv6 route.
 */
public final class PreferIpv4Dns implements Dns {
    public static final PreferIpv4Dns INSTANCE = new PreferIpv4Dns();

    private PreferIpv4Dns() { }

    @Override
    public List<InetAddress> lookup(String hostname) throws UnknownHostException {
        return ipv4First(Dns.SYSTEM.lookup(hostname));
    }

    static List<InetAddress> ipv4First(List<InetAddress> addresses) {
        List<InetAddress> ordered = new ArrayList<>(addresses.size());
        for (InetAddress address : addresses) {
            if (address instanceof Inet4Address) {
                ordered.add(address);
            }
        }
        for (InetAddress address : addresses) {
            if (!(address instanceof Inet4Address)) {
                ordered.add(address);
            }
        }
        return ordered;
    }
}
