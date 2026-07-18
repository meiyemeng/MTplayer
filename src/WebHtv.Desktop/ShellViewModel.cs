using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Media;
using WebHtv.Catalogue;
using WebHtv.Core.Catalogue;
using WebHtv.Spider;
using WebHtv.Configuration;
using WebHtv.Core.Configuration;

namespace WebHtv.Desktop;

internal sealed class ShellViewModel : INotifyPropertyChanged
{
    private const int MaximumRemoteConfigurationBytes = 10 * 1024 * 1024;
    private static readonly HttpClient ConfigurationHttpClient = new(new HttpClientHandler { UseProxy = true })
    {
        Timeout = TimeSpan.FromSeconds(20),
        MaxResponseContentBufferSize = MaximumRemoteConfigurationBytes
    };
    private static readonly HttpClient DirectConfigurationHttpClient = new(new HttpClientHandler { UseProxy = false })
    {
        Timeout = TimeSpan.FromSeconds(20),
        MaxResponseContentBufferSize = MaximumRemoteConfigurationBytes
    };
    private static readonly HttpClient PlaybackProbeHttpClient = new(new HttpClientHandler { UseProxy = false })
    {
        Timeout = TimeSpan.FromSeconds(12)
    };
    private static readonly ParserResolver ParserResolver = new(new HttpClient(new HttpClientHandler { UseProxy = false })
    {
        Timeout = TimeSpan.FromSeconds(15)
    });

    private readonly IConfigurationStore _configurationStore;
    private readonly PosterWallPreferencesStore _posterWallPreferencesStore;
    private readonly LibraryStore _libraryStore;
    private readonly AppSettingsStore _settingsStore;
    private readonly LivePlaylistService _livePlaylistService;
    private readonly IReadOnlyList<ITvBoxCatalogueProvider> _catalogueProviders;
    private AppSettings _settings = new();
    private string _statusMessage = "正在检查本地配置…";
    private string _configurationAddress = string.Empty;
    private string _searchKeyword = string.Empty;
    private bool _isPosterSettingsVisible;
    private double _posterWidth = 156;
    private bool _showTopLists = true;

    static ShellViewModel()
    {
        ConfigurationHttpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/124.0 Safari/537.36");
        ConfigurationHttpClient.DefaultRequestHeaders.Accept.ParseAdd("*/*");
        DirectConfigurationHttpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/124.0 Safari/537.36");
        DirectConfigurationHttpClient.DefaultRequestHeaders.Accept.ParseAdd("*/*");
        PlaybackProbeHttpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/124.0 Safari/537.36");
    }

    private ShellViewModel(
        IConfigurationStore configurationStore,
        PosterWallPreferencesStore posterWallPreferencesStore,
        LibraryStore libraryStore,
        AppSettingsStore settingsStore,
        LivePlaylistService livePlaylistService,
        IReadOnlyList<ITvBoxCatalogueProvider> catalogueProviders)
    {
        _configurationStore = configurationStore;
        _posterWallPreferencesStore = posterWallPreferencesStore;
        _libraryStore = libraryStore;
        _settingsStore = settingsStore;
        _livePlaylistService = livePlaylistService;
        _catalogueProviders = catalogueProviders;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string StatusMessage
    {
        get => _statusMessage;
        private set
        {
            if (_statusMessage == value)
            {
                return;
            }

            _statusMessage = value;
            OnPropertyChanged();
        }
    }

    public string ConfigurationAddress
    {
        get => _configurationAddress;
        set
        {
            if (_configurationAddress == value)
            {
                return;
            }

            _configurationAddress = value;
            OnPropertyChanged();
        }
    }

    public string SearchKeyword
    {
        get => _searchKeyword;
        set
        {
            if (_searchKeyword == value)
            {
                return;
            }

            _searchKeyword = value;
            OnPropertyChanged();
        }
    }

    public bool IsPosterSettingsVisible
    {
        get => _isPosterSettingsVisible;
        private set
        {
            if (_isPosterSettingsVisible == value)
            {
                return;
            }

            _isPosterSettingsVisible = value;
            OnPropertyChanged();
        }
    }

    public double PosterWidth
    {
        get => _posterWidth;
        private set
        {
            if (_posterWidth == value)
            {
                return;
            }

            _posterWidth = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PosterHeight));
        }
    }

    public double PosterHeight => PosterWidth * 1.48;

    public bool ShowTopLists
    {
        get => _showTopLists;
        private set { if (_showTopLists == value) return; _showTopLists = value; OnPropertyChanged(); }
    }

    public void ShowHomeTopLists() => ShowTopLists = true;

    public ObservableCollection<PosterCard> PosterWall { get; } = [];

    public ObservableCollection<PosterCard> TopMovies { get; } = [];

    public ObservableCollection<PosterCard> TopSeries { get; } = [];

    public ObservableCollection<PosterCard> TopAnime { get; } = [];

    public ObservableCollection<PosterCard> TopAnimeSeries { get; } = [];

    public ObservableCollection<PosterCard> TopVariety { get; } = [];

    public ObservableCollection<PosterCard> LibraryCards { get; } = [];

    public ObservableCollection<LiveChannel> LiveChannels { get; } = [];

    public ObservableCollection<SiteOption> SiteOptions { get; } = [];

    public ObservableCollection<ConfigurationSourceEntry> ConfigurationSources { get; } = [];

    public ObservableCollection<CustomLiveSourceEntry> CustomLiveSources { get; } = [];

    public AppSettings Settings => _settings;

    public bool LastConfigurationImportSucceeded { get; private set; }

    public static ShellViewModel CreateDefault()
    {
        var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        var gatewayProvider = new SpiderGatewayProvider(new HttpClient { Timeout = TimeSpan.FromSeconds(45) });
        return new ShellViewModel(
            new AtomicFileConfigurationStore(ApplicationPaths.ConfigurationFilePath),
            new PosterWallPreferencesStore(ApplicationPaths.PosterWallPreferencesFilePath),
            new LibraryStore(ApplicationPaths.LibraryFilePath),
            new AppSettingsStore(ApplicationPaths.SettingsFilePath),
            new LivePlaylistService(),
            [new HttpTvBoxCatalogueProvider(httpClient), new JintSpiderProvider(httpClient), gatewayProvider]);
    }

    public async Task LoadAsync()
    {
        _settings = await _settingsStore.LoadAsync();
        ConfigureSpiderGateway();
        RefreshManagedSources();
        try
        {
            PosterWidth = await _posterWallPreferencesStore.LoadWidthAsync();
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            PosterWidth = 156;
        }

        var activeSource = _settings.ConfigurationSources.FirstOrDefault(item => item.Id == _settings.ActiveConfigurationSourceId)
            ?? _settings.ConfigurationSources.FirstOrDefault();
        if (activeSource is null)
        {
            ConfigurationAddress = string.Empty;
            SiteOptions.Clear();
            StatusMessage = "尚未添加配置源。请在设置中添加 HTTP 或 HTTPS TVBox 配置地址。";
            return;
        }
        ConfigurationAddress = activeSource.Address;

        try
        {
            var document = await _configurationStore.LoadAsync();
            if (IsEmpty(document))
            {
                await ImportFromAddressAsync();
                return;
            }

            if (IsEmpty(document))
            {
                StatusMessage = "尚未导入配置。导入后会在首页生成海报墙。";
            }
            else
            {
                var profile = TvBoxProfileParser.Parse(document.SourceText).Profile;
                if (profile is not null) UpdateSiteOptions(profile);
                StatusMessage = profile is null
                    ? "本地配置无法识别。请重新导入兼容的 TVBox 配置。"
                    : DescribeImportedProfile(profile, document.SavedAtUtc.LocalDateTime.ToString("g", CultureInfo.CurrentCulture));
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or System.Text.Json.JsonException)
        {
            StatusMessage = "无法读取本地配置。请导入有效的 JSON 配置进行替换。";
        }
    }

    public async Task ImportFromFileAsync(string filePath)
    {
        try
        {
            var sourceText = await File.ReadAllTextAsync(filePath);
            await SaveImportedConfigurationAsync(sourceText, $"“{Path.GetFileName(filePath)}”");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            StatusMessage = $"导入失败：{exception.Message}";
        }
    }

    public async Task ImportFromAddressAsync()
    {
        LastConfigurationImportSucceeded = false;
        if (!Uri.TryCreate(ConfigurationAddress.Trim(), UriKind.Absolute, out var address) ||
            (!string.Equals(address.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(address.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            StatusMessage = "请输入有效的 http:// 或 https:// 配置地址。";
            return;
        }

        try
        {
            var sourceText = await DownloadRemoteConfigurationAsync(address);
            if (System.Text.Encoding.UTF8.GetByteCount(sourceText) > MaximumRemoteConfigurationBytes)
            {
                StatusMessage = "网络配置超过 10 MB，已取消导入。";
                return;
            }

            await SaveImportedConfigurationAsync(sourceText, address.Host);
            LastConfigurationImportSucceeded = true;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            StatusMessage = $"无法从网络地址导入：{exception.Message}";
        }
    }

    public async Task UpdateActiveConfigurationSourceAsync(string address)
    {
        LastConfigurationImportSucceeded = false;
        var trimmedAddress = address.Trim();
        if (!Uri.TryCreate(trimmedAddress, UriKind.Absolute, out var parsedAddress) ||
            (!string.Equals(parsedAddress.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(parsedAddress.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            StatusMessage = "请输入有效的 http:// 或 https:// 配置地址。";
            return;
        }

        var activeSource = _settings.ConfigurationSources.FirstOrDefault(item => item.Id == _settings.ActiveConfigurationSourceId);
        if (activeSource is null)
        {
            StatusMessage = "当前没有可更新的配置源。";
            return;
        }

        try
        {
            activeSource.Address = trimmedAddress;
            ConfigurationAddress = trimmedAddress;
            await _settingsStore.SaveAsync(_settings);
            RefreshManagedSources();
            await ImportFromAddressAsync();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            StatusMessage = $"保存配置源地址失败：{exception.Message}";
        }
    }

    public async Task AddConfigurationSourceAsync(string name, string address)
    {
        if (string.IsNullOrWhiteSpace(name) || !Uri.TryCreate(address.Trim(), UriKind.Absolute, out _))
        {
            StatusMessage = "请输入配置源名称和有效的网络地址。";
            return;
        }
        var entry = new ConfigurationSourceEntry { Name = name.Trim(), Address = address.Trim() };
        _settings.ConfigurationSources.Add(entry);
        _settings.ActiveConfigurationSourceId = entry.Id;
        ConfigurationAddress = entry.Address;
        await _settingsStore.SaveAsync(_settings);
        RefreshManagedSources();
        await ImportFromAddressAsync();
    }

    public async Task ActivateConfigurationSourceAsync(ConfigurationSourceEntry entry)
    {
        _settings.ActiveConfigurationSourceId = entry.Id;
        ConfigurationAddress = entry.Address;
        await _settingsStore.SaveAsync(_settings);
        await ImportFromAddressAsync();
        RefreshManagedSources();
    }

    public async Task RemoveConfigurationSourceAsync(ConfigurationSourceEntry entry)
    {
        _settings.ConfigurationSources.RemoveAll(item => item.Id == entry.Id);
        if (_settings.ActiveConfigurationSourceId == entry.Id)
            _settings.ActiveConfigurationSourceId = _settings.ConfigurationSources.FirstOrDefault()?.Id ?? string.Empty;
        await _settingsStore.SaveAsync(_settings);
        RefreshManagedSources();
        if (_settings.ConfigurationSources.Count == 0)
        {
            ConfigurationAddress = string.Empty;
            SiteOptions.Clear();
            ClearTopLists();
            PosterWall.Clear();
            StatusMessage = "配置源已全部删除。";
            return;
        }
        var active = _settings.ConfigurationSources.First(item => item.Id == _settings.ActiveConfigurationSourceId);
        ConfigurationAddress = active.Address;
        await ImportFromAddressAsync();
    }

    public async Task AddCustomLiveSourceAsync(string name, string address, string? epgAddress)
    {
        if (string.IsNullOrWhiteSpace(name) || !Uri.TryCreate(address.Trim(), UriKind.Absolute, out _))
        {
            StatusMessage = "请输入直播源名称和有效的 M3U/M3U8/TXT 地址。";
            return;
        }
        _settings.CustomLiveSources.Add(new CustomLiveSourceEntry { Name = name.Trim(), Address = address.Trim(), EpgAddress = string.IsNullOrWhiteSpace(epgAddress) ? null : epgAddress.Trim() });
        await _settingsStore.SaveAsync(_settings);
        RefreshManagedSources();
        StatusMessage = $"已添加直播源“{name.Trim()}”。";
    }

    public async Task RemoveCustomLiveSourceAsync(CustomLiveSourceEntry entry)
    {
        _settings.CustomLiveSources.RemoveAll(item => item.Id == entry.Id);
        await _settingsStore.SaveAsync(_settings);
        RefreshManagedSources();
    }

    public async Task SearchAsync()
    {
        ShowTopLists = false;
        PosterWall.Clear();
        if (string.IsNullOrWhiteSpace(SearchKeyword))
        {
            StatusMessage = "请输入要搜索的片名、演员或关键词。";
            return;
        }

        try
        {
            var profile = await LoadCurrentProfileAsync();
            if (profile is null)
            {
                StatusMessage = "请先导入兼容的 TVBox 配置。";
                return;
            }

            var executableSites = profile.Sites
                .Where(site => !_settings.DisabledSiteKeys.Contains(site.RuntimeKey, StringComparer.OrdinalIgnoreCase))
                .Select(site => (Site: site, Provider: _catalogueProviders.FirstOrDefault(provider => provider.CanHandle(site))))
                .Where(entry => entry.Provider is not null)
                .Select(entry => (entry.Site, Provider: entry.Provider!))
                .OrderBy(entry => entry.Provider is HttpTvBoxCatalogueProvider ? 0 : 1)
                .ToList();
            if (executableSites.Count > 0)
            {
                StatusMessage = $"正在查询 {executableSites.Count} 个可执行站点…";
                var searchTasks = executableSites.Select(async entry =>
                {
                    try
                    {
                        var page = await entry.Provider.SearchAsync(entry.Site, SearchKeyword.Trim(), 1);
                        return (entry.Site, Page: page, Succeeded: true);
                    }
                    catch (Exception)
                    {
                        return (entry.Site, Page: new CataloguePage([]), Succeeded: false);
                    }
                });
                var searchResults = await Task.WhenAll(searchTasks);
                var successfulSites = searchResults.Count(result => result.Succeeded);
                foreach (var result in searchResults)
                {
                    foreach (var item in result.Page.Items)
                    {
                        PosterWall.Add(ToPosterCard(item with { SourceKey = result.Site.RuntimeKey }));
                    }
                }

                StatusMessage = $"已查询 {successfulSites}/{executableSites.Count} 个站点，汇总 {PosterWall.Count} 条结果。";
                return;
            }

            TvBoxSite? site = null;
            ITvBoxCatalogueProvider? provider = null;
            foreach (var candidate in profile.Sites)
            {
                provider = _catalogueProviders.FirstOrDefault(item => item.CanHandle(candidate));
                if (provider is not null)
                {
                    site = candidate;
                    break;
                }
            }

            if (site is null || provider is null)
            {
                StatusMessage = "没有可执行站点。CSP/JAR 站点需先在设置中填写已开启的 Android Spider Gateway 地址与令牌。";
                return;
            }

            StatusMessage = $"正在通过“{site.Name}”搜索…";
            var page = await provider.SearchAsync(site, SearchKeyword.Trim(), 1);
            PosterWall.Clear();
            foreach (var item in page.Items)
            {
                PosterWall.Add(ToPosterCard(item));
            }

            StatusMessage = page.Items.Count == 0
                ? $"“{site.Name}”没有找到“{SearchKeyword}”的结果。"
                : $"“{site.Name}”找到 {page.Items.Count} 条结果。";
        }
        catch (Exception exception)
        {
            StatusMessage = $"搜索失败：{exception.Message}";
        }
    }

    public async Task LoadTopListsAsync()
    {
        ClearTopLists();
        try
        {
            var profile = await LoadCurrentProfileAsync();
            if (profile is null) return;
            var sites = profile.Sites
                .Where(site => !_settings.DisabledSiteKeys.Contains(site.RuntimeKey, StringComparer.OrdinalIgnoreCase))
                .Select(site => (Site: site, Provider: _catalogueProviders.FirstOrDefault(provider => provider.CanHandle(site))))
                .Where(entry => entry.Provider is not null)
                .Select(entry => (entry.Site, Provider: entry.Provider!))
                .OrderBy(entry => entry.Provider is HttpTvBoxCatalogueProvider ? 0 : 1)
                .Take(4)
                .ToArray();
            if (sites.Length == 0)
            {
                StatusMessage = "没有可用于首页的站点；CSP/JAR 配置请先连接 Android Spider Gateway。";
                return;
            }

            StatusMessage = "正在准备首页 Top 10…";
            var categories = new[]
            {
                (Keyword: "电影", Fallback: "电影", Target: TopMovies),
                (Keyword: "电视剧", Fallback: "连续剧", Target: TopSeries),
                (Keyword: "动漫电影", Fallback: "动漫", Target: TopAnime),
                (Keyword: "番剧", Fallback: "动漫", Target: TopAnimeSeries),
                (Keyword: "综艺", Fallback: "真人秀", Target: TopVariety)
            };
            var tasks = categories.Select(async category =>
            {
                var queries = sites.SelectMany(entry => new[] { category.Keyword, category.Fallback }.Distinct(StringComparer.OrdinalIgnoreCase).Select(async keyword =>
                {
                    try
                    {
                        var page = await entry.Provider.SearchAsync(entry.Site, keyword, 1);
                        return page.Items.Select(item => ToPosterCard(item with { SourceKey = entry.Site.RuntimeKey })).ToArray();
                    }
                    catch (Exception)
                    {
                        return [];
                    }
                }));
                var cards = (await Task.WhenAll(queries)).SelectMany(items => items)
                    .DistinctBy(card => card.Title, StringComparer.OrdinalIgnoreCase)
                    .Take(10)
                    .ToArray();
                return (category.Target, Cards: cards);
            });

            foreach (var result in await Task.WhenAll(tasks))
            {
                result.Target.Clear();
                foreach (var card in result.Cards) result.Target.Add(card);
            }
            StatusMessage = $"首页已准备 {TopMovies.Count + TopSeries.Count + TopAnime.Count + TopAnimeSeries.Count + TopVariety.Count} 部热门内容。";
        }
        catch (Exception exception)
        {
            StatusMessage = $"首页 Top 10 加载失败：{exception.Message}";
        }
    }

    private void ClearTopLists()
    {
        TopMovies.Clear();
        TopSeries.Clear();
        TopAnime.Clear();
        TopAnimeSeries.Clear();
        TopVariety.Clear();
    }

    public async Task<PlayRequest?> GetFirstPlayRequestAsync(PosterCard card)
    {
        var context = await LoadDetailAsync(card);
        if (context is null) return null;
        var sourceIndex = GetPreferredSourceIndex(context);
        return await ResolvePlayRequestAsync(context, sourceIndex, 0);
    }

    public async Task<MovieDetailContext?> LoadDetailAsync(PosterCard card)
    {
        try
        {
            var profile = await LoadCurrentProfileAsync();
            var site = profile?.Sites.FirstOrDefault(item => item.RuntimeKey == card.SourceKey);
            var provider = site is null ? null : _catalogueProviders.FirstOrDefault(item => item.CanHandle(site));
            if (site is null || provider is null) throw new InvalidOperationException("当前结果对应的站点不可用。");
            var detail = await provider.GetDetailAsync(site, card.Id);
            if (detail.Sources.Count == 0) throw new InvalidDataException("详情没有可播放的选集。");
            return new MovieDetailContext(card, detail, profile!, site, provider);
        }
        catch (Exception exception)
        {
            StatusMessage = $"无法加载详情：{exception.Message}";
            return null;
        }
    }

    public async Task<IReadOnlyList<PlaybackInterfaceOption>> FindAvailablePlaybackInterfacesAsync(MovieDetailContext original)
    {
        using var originalTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(9));
        var originalProbe = HasReachablePlaybackAsync(original, originalTimeout.Token);
        var candidates = original.Profile.Sites
            .Where(site => site.RuntimeKey != original.Site.RuntimeKey)
            .Where(site => site.Searchable is not 0)
            .Where(site => !_settings.DisabledSiteKeys.Contains(site.RuntimeKey, StringComparer.OrdinalIgnoreCase))
            .Select(site => (Site: site, Provider: _catalogueProviders.FirstOrDefault(provider => provider.CanHandle(site))))
            .Where(entry => entry.Provider is not null)
            .Select(entry => (entry.Site, Provider: entry.Provider!))
            .Take(48)
            .ToArray();

        var probes = candidates.Select(async entry =>
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(9));
            try
            {
                var page = await entry.Provider.SearchAsync(entry.Site, original.Card.Title, 1, timeout.Token);
                var item = FindMatchingItem(page.Items, original.Card.Title);
                if (item is null) return null;

                var detail = await entry.Provider.GetDetailAsync(entry.Site, item.Id, timeout.Token);
                if (!detail.Sources.Any(source => source.Episodes.Count > 0)) return null;

                var card = ToPosterCard(item with { SourceKey = entry.Site.RuntimeKey });
                var context = new MovieDetailContext(card, detail, original.Profile, entry.Site, entry.Provider);
                if (!await HasReachablePlaybackAsync(context, timeout.Token)) return null;
                return new PlaybackInterfaceOption(entry.Site.Name, entry.Site.RuntimeKey, context);
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidDataException or JsonException or ArgumentException)
            {
                return null;
            }
            catch (Exception)
            {
                return null;
            }
        });

        var available = (await Task.WhenAll(probes))
            .Where(option => option is not null)
            .Cast<PlaybackInterfaceOption>()
            .OrderBy(option => option.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        if (await originalProbe)
            available.Insert(0, new PlaybackInterfaceOption(original.Site.Name, original.Site.RuntimeKey, original));
        return available;
    }

    private static async Task<bool> HasReachablePlaybackAsync(MovieDetailContext context, CancellationToken cancellationToken)
    {
        var sources = context.Detail.Sources
            .Where(source => source.Episodes.Count > 0)
            .Take(4)
            .ToArray();

        foreach (var source in sources)
        {
            PlayRequest originalRequest;
            try
            {
                originalRequest = context.Provider is IAsyncPlayRequestProvider asyncProvider
                    ? await asyncProvider.CreatePlayRequestAsync(context.Site, source, source.Episodes[0], cancellationToken)
                    : context.Provider.CreatePlayRequest(context.Site, source, source.Episodes[0]);
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidDataException)
            {
                continue;
            }
            var request = originalRequest.RequiresParser
                ? await ParserResolver.ResolveAsync(context.Profile.Parses, originalRequest, cancellationToken)
                : originalRequest;
            if (request is null && originalRequest.RequiresParser &&
                Uri.TryCreate(originalRequest.Url, UriKind.Absolute, out var parserAddress) &&
                (parserAddress.Scheme == Uri.UriSchemeHttp || parserAddress.Scheme == Uri.UriSchemeHttps))
            {
                // WebView2 can execute a browser parser and capture its media request.
                return true;
            }
            if (request is null || request.RequiresParser ||
                !Uri.TryCreate(request.Url, UriKind.Absolute, out var address) ||
                (address.Scheme != Uri.UriSchemeHttp && address.Scheme != Uri.UriSchemeHttps)) continue;

            try
            {
                using var message = new HttpRequestMessage(HttpMethod.Get, address);
                foreach (var header in request.Headers)
                    message.Headers.TryAddWithoutValidation(header.Key, header.Value);
                using var response = await PlaybackProbeHttpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (response.IsSuccessStatusCode) return true;
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or ArgumentException)
            {
                // Try the next direct line before hiding this interface.
            }
        }

        return false;
    }

    private static CatalogueItem? FindMatchingItem(IReadOnlyList<CatalogueItem> items, string title)
    {
        var normalizedTitle = NormalizeTitle(title);
        return items
            .Select(item => (Item: item, Normalized: NormalizeTitle(item.Title)))
            .Where(entry => entry.Normalized == normalizedTitle ||
                            (normalizedTitle.Length >= 2 && entry.Normalized.Length >= 2 &&
                             (entry.Normalized.Contains(normalizedTitle, StringComparison.OrdinalIgnoreCase) ||
                              normalizedTitle.Contains(entry.Normalized, StringComparison.OrdinalIgnoreCase)) &&
                             Math.Abs(entry.Normalized.Length - normalizedTitle.Length) <= 8))
            .OrderBy(entry => entry.Normalized == normalizedTitle ? 0 : Math.Abs(entry.Normalized.Length - normalizedTitle.Length))
            .Select(entry => entry.Item)
            .FirstOrDefault();
    }

    private static string NormalizeTitle(string title) =>
        Regex.Replace(title, @"[\p{P}\p{S}\s]+", string.Empty).ToUpperInvariant();

    public static int GetPreferredSourceIndex(MovieDetailContext context)
    {
        if (context.Provider is IAsyncPlayRequestProvider) return 0;
        for (var index = 0; index < context.Detail.Sources.Count; index++)
        {
            var source = context.Detail.Sources[index];
            if (source.Episodes.Count == 0) continue;
            if (!context.Provider.CreatePlayRequest(context.Site, source, source.Episodes[0]).RequiresParser) return index;
        }
        return 0;
    }

    public async Task<PlayRequest?> ResolvePlayRequestAsync(MovieDetailContext context, int sourceIndex, int episodeIndex, TvBoxParser? selectedParser = null)
    {
        try
        {
            if (sourceIndex < 0 || sourceIndex >= context.Detail.Sources.Count) throw new ArgumentOutOfRangeException(nameof(sourceIndex));
            var source = context.Detail.Sources[sourceIndex];
            if (episodeIndex < 0 || episodeIndex >= source.Episodes.Count) throw new ArgumentOutOfRangeException(nameof(episodeIndex));
            var episode = source.Episodes[episodeIndex];
            var request = context.Provider is IAsyncPlayRequestProvider asyncProvider
                ? await asyncProvider.CreatePlayRequestAsync(context.Site, source, episode)
                : context.Provider.CreatePlayRequest(context.Site, source, episode);
            if (request.RequiresParser)
            {
                IReadOnlyList<TvBoxParser> parsers = selectedParser is null ? context.Profile.Parses : [selectedParser];
                var resolvedRequest = await ParserResolver.ResolveAsync(parsers, request);
                if (resolvedRequest is not null)
                {
                    return resolvedRequest;
                }

                var browserParser = parsers.FirstOrDefault(parser => parser.Type == 0 && !string.IsNullOrWhiteSpace(parser.Url));
                if (browserParser is not null)
                {
                    return request with { Url = ParserResolver.BuildAddress(browserParser.Url!, request.Url) };
                }

                if (Uri.TryCreate(request.Url, UriKind.Absolute, out var browserAddress) &&
                    (browserAddress.Scheme == Uri.UriSchemeHttp || browserAddress.Scheme == Uri.UriSchemeHttps))
                {
                    // Some Android spiders already return their own browser parser URL.
                    // Let WebView2 execute it and capture the real media request.
                    return request;
                }

                StatusMessage = "该线路需要解析器；原生解析器正在接入。";
                return null;
            }

            return request;
        }
        catch (Exception exception)
        {
            StatusMessage = $"无法播放：{exception.Message}";
            return null;
        }
    }

    public async Task<bool> IsFavoriteAsync(PosterCard card)
    {
        var document = await _libraryStore.LoadAsync();
        return document.Favorites.Any(item => item.SourceKey == card.SourceKey && item.Id == card.Id);
    }

    public async Task<bool> ToggleFavoriteAsync(PosterCard card)
    {
        var added = await _libraryStore.ToggleFavoriteAsync(card);
        await LoadFavoritesAsync();
        StatusMessage = added ? $"已收藏《{card.Title}》。" : $"已取消收藏《{card.Title}》。";
        return added;
    }

    public async Task LoadFavoritesAsync()
    {
        var document = await _libraryStore.LoadAsync();
        LibraryCards.Clear();
        foreach (var item in document.Favorites)
            LibraryCards.Add(CreatePosterCard(item.SourceKey, item.Id, item.Category, item.Title, item.Caption, item.CoverUrl));
        StatusMessage = LibraryCards.Count == 0 ? "还没有收藏。打开影片详情后可以加入收藏。" : $"共 {LibraryCards.Count} 条收藏。";
    }

    public async Task LoadHistoryAsync()
    {
        var document = await _libraryStore.LoadAsync();
        LibraryCards.Clear();
        foreach (var item in document.History)
        {
            var progress = item.DurationMs > 0 ? $"已看 {Math.Clamp(item.PositionMs * 100 / item.DurationMs, 0, 100)}%" : item.Caption;
            LibraryCards.Add(CreatePosterCard(item.SourceKey, item.Id, item.Category, item.Title, progress, item.CoverUrl));
        }
        StatusMessage = LibraryCards.Count == 0 ? "还没有观看记录。" : $"共 {LibraryCards.Count} 条观看记录。";
    }

    public async Task<HistoryEntry?> GetHistoryAsync(PosterCard card)
    {
        var document = await _libraryStore.LoadAsync();
        return document.History.FirstOrDefault(item => item.SourceKey == card.SourceKey && item.Id == card.Id);
    }

    public Task SaveHistoryAsync(PosterCard card, int sourceIndex, int episodeIndex, long positionMs, long durationMs) =>
        _libraryStore.SaveHistoryAsync(card, sourceIndex, episodeIndex, positionMs, durationMs);

    public Task<SkipMarker?> GetSkipMarkerAsync(PosterCard card, string lineName) =>
        _libraryStore.GetSkipMarkerAsync(card.SourceKey, card.Id, lineName);

    public Task SaveSkipMarkerAsync(
        PosterCard card,
        string lineName,
        long introEndMs,
        long outroStartMs,
        long durationMs) =>
        _libraryStore.SaveSkipMarkerAsync(
            card.SourceKey,
            card.Id,
            lineName,
            introEndMs,
            outroStartMs,
            durationMs);

    public async Task ClearHistoryAsync()
    {
        await _libraryStore.ClearHistoryAsync();
        await LoadHistoryAsync();
    }

    public async Task LoadLiveChannelsAsync()
    {
        var profile = await LoadCurrentProfileAsync();
        if (profile is null) { StatusMessage = "请先导入包含直播源的配置。"; return; }
        StatusMessage = $"正在读取 {profile.Lives.Count} 个直播源…";
        var customSources = _settings.CustomLiveSources.Select(item => new TvBoxLive { Name = item.Name, Url = item.Address });
        var channels = await _livePlaylistService.LoadAsync(profile.Lives.Concat(customSources));
        channels = await _livePlaylistService.EnrichWithEpgAsync(channels, _settings.CustomLiveSources.Select(item => item.EpgAddress).OfType<string>());
        LiveChannels.Clear();
        foreach (var channel in channels) LiveChannels.Add(channel);
        StatusMessage = channels.Count == 0 ? "没有读取到可用直播频道。" : $"已读取 {channels.Count} 个直播频道。";
    }

    public async Task SaveSettingsAsync(AppSettings settings)
    {
        settings.DisabledSiteKeys = SiteOptions.Where(item => !item.IsEnabled).Select(item => item.RuntimeKey).ToList();
        _settings = settings;
        ConfigureSpiderGateway();
        await _settingsStore.SaveAsync(settings);
        StatusMessage = "设置已保存。";
    }

    public void TogglePosterSettings() => IsPosterSettingsVisible = !IsPosterSettingsVisible;

    public async Task SetPosterWidthAsync(double width, string density)
    {
        PosterWidth = width;
        try
        {
            await _posterWallPreferencesStore.SaveWidthAsync(width);
            StatusMessage = $"海报墙已调整为“{density}”密度，并会在下次启动时保留。";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            StatusMessage = $"海报墙已调整为“{density}”密度，但无法保存该偏好：{exception.Message}";
        }
    }

    private static async Task<string> DownloadWithCurlIfRequiredAsync(Uri address, string sourceText)
    {
        if (!sourceText.TrimStart().StartsWith('<'))
        {
            return sourceText;
        }

        try
        {
            var startInfo = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "curl.exe"))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add("--silent");
            startInfo.ArgumentList.Add("--show-error");
            startInfo.ArgumentList.Add("--insecure");
            startInfo.ArgumentList.Add("--location");
            startInfo.ArgumentList.Add("--max-time");
            startInfo.ArgumentList.Add("20");
            startInfo.ArgumentList.Add(address.AbsoluteUri);

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return sourceText;
            }

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            await Task.WhenAll(process.WaitForExitAsync(), outputTask, errorTask);
            var output = await outputTask;
            if (process.ExitCode != 0 || output.TrimStart().StartsWith('<'))
            {
                throw new InvalidDataException("The network endpoint rejected the Windows curl request.");
            }

            return output;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return sourceText;
        }
    }

    private static async Task<string> DownloadRemoteConfigurationAsync(Uri address)
    {
        var failures = new List<Exception>();
        foreach (var client in new[] { ConfigurationHttpClient, DirectConfigurationHttpClient })
        {
            try
            {
                using var response = await client.GetAsync(address, HttpCompletionOption.ResponseContentRead);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength is > MaximumRemoteConfigurationBytes)
                {
                    throw new InvalidDataException("网络配置超过 10 MB。");
                }

                var sourceText = await response.Content.ReadAsStringAsync();
                if (System.Text.Encoding.UTF8.GetByteCount(sourceText) > MaximumRemoteConfigurationBytes)
                {
                    throw new InvalidDataException("网络配置超过 10 MB。");
                }

                if (!sourceText.TrimStart().StartsWith('<'))
                {
                    return sourceText;
                }

                failures.Add(new InvalidDataException("服务器返回了网页，而不是 TVBox 配置。"));
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException or InvalidDataException)
            {
                failures.Add(exception);
            }
        }

        try
        {
            var sourceText = await DownloadWithCurlIfRequiredAsync(address, "<");
            if (!sourceText.TrimStart().StartsWith('<'))
            {
                return sourceText;
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            failures.Add(exception);
        }

        var lastFailure = failures.LastOrDefault();
        throw new HttpRequestException(
            $"配置源连接失败（已尝试系统代理和直连）：{lastFailure?.Message ?? "没有收到有效配置"}",
            lastFailure);
    }

    private static async Task<ConfigurationDocument> ExpandDepotAsync(ConfigurationDocument document)
    {
        var rootProfile = TvBoxProfileParser.Parse(document.SourceText).Profile;
        if (rootProfile is null || !rootProfile.IsDepot)
        {
            return document;
        }

        var profiles = new List<TvBoxProfile> { rootProfile };
        var importedAddresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in rootProfile.Urls)
        {
            if (!Uri.TryCreate(entry.Url, UriKind.Absolute, out var address) || !importedAddresses.Add(address.AbsoluteUri))
            {
                continue;
            }

            try
            {
                var sourceText = await DownloadRemoteConfigurationAsync(address);
                if (System.Text.Encoding.UTF8.GetByteCount(sourceText) > MaximumRemoteConfigurationBytes)
                {
                    continue;
                }

                var importedDocument = ConfigurationImporter.Import(sourceText);
                var importedProfile = TvBoxProfileParser.Parse(importedDocument.SourceText).Profile;
                if (importedProfile is not null)
                {
                    profiles.Add(importedProfile);
                }
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or InvalidOperationException)
            {
                // A single unavailable depot entry must not prevent the rest of the group from loading.
            }
        }

        if (profiles.Count == 1)
        {
            return document;
        }

        var mergedProfile = new TvBoxProfile
        {
            Spider = rootProfile.Spider,
            Wallpaper = rootProfile.Wallpaper,
            Logo = rootProfile.Logo,
            Notice = rootProfile.Notice,
            Danmaku = rootProfile.Danmaku,
            Sites = profiles.SelectMany(profile => profile.Sites).ToList(),
            Lives = profiles.SelectMany(profile => profile.Lives).ToList(),
            Parses = profiles.SelectMany(profile => profile.Parses).ToList(),
            Flags = profiles.SelectMany(profile => profile.Flags).Distinct(StringComparer.Ordinal).ToList(),
            Rules = rootProfile.Rules,
            Headers = rootProfile.Headers,
            Proxy = rootProfile.Proxy,
            Hosts = profiles.SelectMany(profile => profile.Hosts).Distinct(StringComparer.Ordinal).ToList(),
            Ads = profiles.SelectMany(profile => profile.Ads).Distinct(StringComparer.Ordinal).ToList(),
            Doh = rootProfile.Doh,
            WebHomeExtensions = rootProfile.WebHomeExtensions,
            Urls = rootProfile.Urls,
            ExtensionData = rootProfile.ExtensionData
        };

        return ConfigurationImporter.Import(JsonSerializer.Serialize(mergedProfile));
    }

    private async Task SaveImportedConfigurationAsync(string sourceText, string sourceName)
    {
        var document = ConfigurationImporter.Import(sourceText);
        document = await ExpandDepotAsync(document);
        await _configurationStore.SaveAsync(document);
        var profile = TvBoxProfileParser.Parse(document.SourceText).Profile!;
        PosterWall.Clear();
        ClearTopLists();
        UpdateSiteOptions(profile);
        StatusMessage = $"已从 {sourceName} 导入配置。{DescribeImportedProfile(profile, null)}";
    }

    private async Task<TvBoxProfile?> LoadCurrentProfileAsync()
    {
        if (_settings.ConfigurationSources.Count == 0) return null;
        var document = await _configurationStore.LoadAsync();
        return IsEmpty(document) ? null : TvBoxProfileParser.Parse(document.SourceText).Profile;
    }

    private PosterCard ToPosterCard(CatalogueItem item) =>
        CreatePosterCard(item.SourceKey, item.Id, item.TypeName, item.Title, item.Remarks, _settings.UseSourceCovers ? item.CoverUrl : string.Empty);

    private static PosterCard CreatePosterCard(string sourceKey, string id, string category, string title, string caption, string coverUrl)
    {
        var palette = Math.Abs(title.Aggregate(17, (seed, character) => (seed * 31) + character)) % 3;
        var colors = palette switch
        {
            0 => (Color.FromRgb(67, 92, 148), Color.FromRgb(22, 35, 73)),
            1 => (Color.FromRgb(31, 118, 137), Color.FromRgb(19, 42, 68)),
            _ => (Color.FromRgb(126, 78, 130), Color.FromRgb(35, 33, 70))
        };
        return new PosterCard(sourceKey, id, category, title, caption, coverUrl, colors.Item1, colors.Item2);
    }

    private void UpdateSiteOptions(TvBoxProfile profile)
    {
        SiteOptions.Clear();
        foreach (var site in profile.Sites)
            SiteOptions.Add(new SiteOption(site.RuntimeKey, site.Name, site.Type, !_settings.DisabledSiteKeys.Contains(site.RuntimeKey, StringComparer.OrdinalIgnoreCase)));
    }

    private void RefreshManagedSources()
    {
        ConfigurationSources.Clear();
        foreach (var source in _settings.ConfigurationSources) ConfigurationSources.Add(source);
        CustomLiveSources.Clear();
        foreach (var source in _settings.CustomLiveSources) CustomLiveSources.Add(source);
        OnPropertyChanged(nameof(ActiveConfigurationSourceId));
    }

    private void ConfigureSpiderGateway()
    {
        var provider = _catalogueProviders.OfType<SpiderGatewayProvider>().FirstOrDefault();
        provider?.Configure(_settings.SpiderGatewayUrl, _settings.SpiderGatewayToken);
    }

    public string ActiveConfigurationSourceId => _settings.ActiveConfigurationSourceId;

    private static string DescribeImportedProfile(TvBoxProfile profile, string? savedAt) =>
        $"识别到 {profile.Sites.Count} 个点播站点、{profile.Lives.Count} 个直播源、{profile.Parses.Count} 个解析器" +
        (profile.IsDepot ? $"、{profile.Urls.Count} 个仓库入口" : string.Empty) +
        (savedAt is null ? "。" : $"；上次保存于 {savedAt}。 ");

    private static bool IsEmpty(ConfigurationDocument document) => document.SourceText.Trim() == "{}";

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

internal sealed record PosterCard(
    string SourceKey,
    string Id,
    string Category,
    string Title,
    string Caption,
    string CoverUrl,
    Color AccentStart,
    Color AccentEnd);

internal sealed record MovieDetailContext(
    PosterCard Card,
    CatalogueDetail Detail,
    TvBoxProfile Profile,
    TvBoxSite Site,
    ITvBoxCatalogueProvider Provider);

internal sealed class SiteOption(string runtimeKey, string name, int type, bool isEnabled) : INotifyPropertyChanged
{
    private bool _isEnabled = isEnabled;
    public string RuntimeKey { get; } = runtimeKey;
    public string Name { get; } = name;
    public int Type { get; } = type;
    public bool IsEnabled
    {
        get => _isEnabled;
        set { if (_isEnabled == value) return; _isEnabled = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEnabled))); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}
