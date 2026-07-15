package cn.mtplayer.tv;
import android.content.Context;
import java.util.concurrent.TimeUnit;
import cn.mtplayer.core.account.AccountClient;
import cn.mtplayer.core.catalogue.CmsCatalogueClient;
import cn.mtplayer.core.config.ConfigurationRepository;
import okhttp3.OkHttpClient;
public final class TvServices { public static ConfigurationRepository configurations;public static CmsCatalogueClient catalogue;public static AccountClient account;public static void initialize(Context c){OkHttpClient h=new OkHttpClient.Builder().connectTimeout(10,TimeUnit.SECONDS).readTimeout(15,TimeUnit.SECONDS).callTimeout(18,TimeUnit.SECONDS).build();configurations=new ConfigurationRepository(c,h);catalogue=new CmsCatalogueClient(h);account=new AccountClient(c,h);}private TvServices(){} }
