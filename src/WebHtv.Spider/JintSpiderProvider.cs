using Jint;
using WebHtv.Catalogue;
using WebHtv.Core.Catalogue;
using WebHtv.Core.Configuration;

namespace WebHtv.Spider;

/// <summary>Runs trusted TVBox JavaScript spiders without exposing CLR access.</summary>
public sealed class JintSpiderProvider(HttpClient httpClient) : ITvBoxCatalogueProvider
{
    private readonly HttpClient _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));

    public bool CanHandle(TvBoxSite site) => site.Type == 3 && site.Api.EndsWith(".js", StringComparison.OrdinalIgnoreCase);

    public async Task<CataloguePage> SearchAsync(TvBoxSite site, string keyword, int page, CancellationToken cancellationToken = default)
    {
        var result = await InvokeAsync(site, ["search", "searchContent"], [keyword, false, page.ToString(System.Globalization.CultureInfo.InvariantCulture)], cancellationToken);
        return TvBoxJsonResultParser.ParsePage(site.Key, result);
    }

    public async Task<CatalogueDetail> GetDetailAsync(TvBoxSite site, string id, CancellationToken cancellationToken = default)
    {
        var result = await InvokeAsync(site, ["detail", "detailContent"], [id], cancellationToken);
        return TvBoxJsonResultParser.ParseDetail(site.Key, result);
    }

    public PlayRequest CreatePlayRequest(TvBoxSite site, EpisodeSource source, Episode episode) =>
        new(episode.Url, source.Name, true, new Dictionary<string, string>());

    public async Task<string> InvokePlayerAsync(TvBoxSite site, string flag, string id, CancellationToken cancellationToken = default) =>
        await InvokeAsync(site, ["player", "playerContent"], [flag, id, Array.Empty<string>()], cancellationToken);

    private async Task<string> InvokeAsync(TvBoxSite site, IReadOnlyList<string> functionNames, object[] arguments, CancellationToken cancellationToken)
    {
        if (!CanHandle(site)) throw new NotSupportedException("该站点不是 JavaScript Spider。");
        var script = await _httpClient.GetStringAsync(site.Api, cancellationToken);
        var engine = new Engine(options => options
            .LimitMemory(32_000_000)
            .TimeoutInterval(TimeSpan.FromSeconds(12))
            .MaxStatements(500_000)
            .CancellationToken(cancellationToken));
        engine.Execute(script);
        foreach (var name in functionNames)
        {
            if (!engine.GetValue(name).IsUndefined()) return engine.Invoke(name, arguments).ToString();
        }

        throw new InvalidDataException($"Spider 未实现 {string.Join(" / ", functionNames)} 方法。");
    }
}
