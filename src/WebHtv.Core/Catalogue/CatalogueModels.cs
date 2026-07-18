using WebHtv.Core.Configuration;

namespace WebHtv.Core.Catalogue;

public sealed record CatalogueItem(
    string SourceKey,
    string Id,
    string Title,
    string CoverUrl,
    string Remarks,
    string TypeName);

public sealed record CataloguePage(IReadOnlyList<CatalogueItem> Items, int PageCount = 0);

public sealed record Episode(string Name, string Url);

public sealed record EpisodeSource(string Name, IReadOnlyList<Episode> Episodes);

public sealed record CatalogueMetadata(string Year, string Area, string Language, string Director, string Actors, string Score);

public sealed record CatalogueDetail(CatalogueItem Item, IReadOnlyList<EpisodeSource> Sources, string Description, CatalogueMetadata Metadata);

public sealed record PlayRequest(string Url, string Flag, bool RequiresParser, IReadOnlyDictionary<string, string> Headers);

public interface ITvBoxCatalogueProvider
{
    bool CanHandle(TvBoxSite site);

    Task<CataloguePage> SearchAsync(TvBoxSite site, string keyword, int page, CancellationToken cancellationToken = default);

    Task<CatalogueDetail> GetDetailAsync(TvBoxSite site, string id, CancellationToken cancellationToken = default);

    PlayRequest CreatePlayRequest(TvBoxSite site, EpisodeSource source, Episode episode);
}

public interface IAsyncPlayRequestProvider
{
    Task<PlayRequest> CreatePlayRequestAsync(
        TvBoxSite site,
        EpisodeSource source,
        Episode episode,
        CancellationToken cancellationToken = default);
}
