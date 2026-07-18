package cn.mtplayer.mobile.data;

import android.content.Context;
import java.util.concurrent.TimeUnit;
import cn.mtplayer.core.account.AccountClient;
import cn.mtplayer.core.catalogue.CmsCatalogueClient;
import cn.mtplayer.core.config.ConfigurationRepository;
import cn.mtplayer.core.spider.SpiderGatewayServer;
import cn.mtplayer.core.network.PreferIpv4Dns;
import okhttp3.OkHttpClient;

public final class AppServices {
    public static ConfigurationRepository configurations;
    public static SpiderGatewayServer spiderGateway;
    public static CmsCatalogueClient catalogue;
    public static AccountClient account;
    public static OkHttpClient http;
    public static void initialize(Context context) {
        http = new OkHttpClient.Builder()
                .dns(PreferIpv4Dns.INSTANCE)
                .connectTimeout(10, TimeUnit.SECONDS)
                .readTimeout(15, TimeUnit.SECONDS)
                .callTimeout(18, TimeUnit.SECONDS)
                .build();
        configurations = new ConfigurationRepository(context, http);
        spiderGateway = new SpiderGatewayServer(context, http);
        spiderGateway.startIfEnabled();
        catalogue = new CmsCatalogueClient(context, http);
        account = new AccountClient(context, http);
    }
    private AppServices() { }
}
