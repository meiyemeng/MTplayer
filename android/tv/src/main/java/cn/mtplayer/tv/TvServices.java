package cn.mtplayer.tv;
import android.content.Context;
import java.util.concurrent.TimeUnit;
import cn.mtplayer.core.account.AccountClient;
import cn.mtplayer.core.catalogue.CmsCatalogueClient;
import cn.mtplayer.core.config.ConfigurationRepository;
import cn.mtplayer.core.network.PreferIpv4Dns;
import okhttp3.OkHttpClient;

public final class TvServices {
    public static ConfigurationRepository configurations;
    public static CmsCatalogueClient catalogue;
    public static AccountClient account;

    public static void initialize(Context context) {
        OkHttpClient http = new OkHttpClient.Builder()
                .dns(PreferIpv4Dns.INSTANCE)
                .connectTimeout(10, TimeUnit.SECONDS)
                .readTimeout(15, TimeUnit.SECONDS)
                .callTimeout(18, TimeUnit.SECONDS)
                .build();
        configurations = new ConfigurationRepository(context, http);
        catalogue = new CmsCatalogueClient(http);
        account = new AccountClient(context, http);
    }

    private TvServices() { }
}
