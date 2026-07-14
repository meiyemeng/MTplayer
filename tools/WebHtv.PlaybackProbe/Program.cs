using System.Text.Json;
using WebHtv.Catalogue;
using WebHtv.Configuration;
using WebHtv.Core.Catalogue;
using WebHtv.Core.Configuration;
using WebHtv.Spider;
using WebHtv.Playback;

var configurationPath = args.ElementAtOrDefault(0) ?? throw new ArgumentException("Configuration path is required.");
var keyword = args.ElementAtOrDefault(1) ?? "仙逆";
using var outerDocument = JsonDocument.Parse(await File.ReadAllTextAsync(configurationPath));
var sourceText = outerDocument.RootElement.GetProperty("sourceText").GetString() ?? "{}";
var profile = TvBoxProfileParser.Parse(sourceText).Profile ?? throw new InvalidDataException("Profile is invalid.");
using var httpClient = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(15) };
ITvBoxCatalogueProvider[] providers = [new HttpTvBoxCatalogueProvider(httpClient), new JintSpiderProvider(httpClient)];

foreach (var site in profile.Sites)
{
    var provider = providers.FirstOrDefault(item => item.CanHandle(site));
    if (provider is null) continue;
    try
    {
        var page = await provider.SearchAsync(site, keyword, 1);
        if (page.Items.Count == 0) continue;
        var item = page.Items[0];
        var detail = await provider.GetDetailAsync(site, item.Id);
        if (detail.Sources.Count == 0 || detail.Sources[0].Episodes.Count == 0) continue;
        foreach (var candidateSource in detail.Sources)
        {
            foreach (var candidateEpisode in candidateSource.Episodes.Take(3))
            {
                var candidateRequest = provider.CreatePlayRequest(site, candidateSource, candidateEpisode);
                var candidateShape = "non-http";
                var candidateType = "none";
                var candidateStatus = 0;
                if (Uri.TryCreate(candidateRequest.Url, UriKind.Absolute, out var candidateUri))
                {
                    try
                    {
                        using var candidateResponse = await httpClient.GetAsync(candidateUri, HttpCompletionOption.ResponseHeadersRead);
                        var candidateBody = await candidateResponse.Content.ReadAsStringAsync();
                        var candidateTrimmed = candidateBody.TrimStart();
                        candidateStatus = (int)candidateResponse.StatusCode;
                        candidateType = candidateResponse.Content.Headers.ContentType?.MediaType ?? "none";
                        candidateShape = candidateTrimmed.StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase)
                            ? "hls"
                            : candidateTrimmed.StartsWith('<') ? "html" : "other";
                    }
                    catch (Exception exception)
                    {
                        candidateShape = exception.GetType().Name;
                    }
                }

                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    candidateSource = candidateSource.Name,
                    candidateEpisode = candidateEpisode.Name,
                    candidateRequest.RequiresParser,
                    candidateStatus,
                    candidateType,
                    candidateShape,
                    candidateHost = candidateUri?.Host ?? "none"
                }));
            }
        }
        var playable = detail.Sources
            .SelectMany(source => source.Episodes.Take(1).Select(episode => new
            {
                Source = source,
                Episode = episode,
                Request = provider.CreatePlayRequest(site, source, episode)
            }))
            .OrderBy(candidate => candidate.Request.RequiresParser)
            .First();
        var source = playable.Source;
        var episode = playable.Episode;
        var request = playable.Request;
        var host = Uri.TryCreate(request.Url, UriKind.Absolute, out var address) ? address.Host : "non-http";
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            site = site.Name,
            site.Type,
            provider = provider.GetType().Name,
            results = page.Items.Count,
            sources = detail.Sources.Count,
            episodes = source.Episodes.Count,
            request.RequiresParser,
            requestScheme = address?.Scheme ?? "none",
            requestHost = host
        }));
        if (!request.RequiresParser)
        {
            using var mediaResponse = await httpClient.GetAsync(request.Url, HttpCompletionOption.ResponseHeadersRead);
            var mediaBody = await mediaResponse.Content.ReadAsStringAsync();
            var trimmedMediaBody = mediaBody.TrimStart();
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                mediaStatus = (int)mediaResponse.StatusCode,
                contentType = mediaResponse.Content.Headers.ContentType?.MediaType ?? "none",
                contentLength = mediaResponse.Content.Headers.ContentLength,
                finalHost = mediaResponse.RequestMessage?.RequestUri?.Host ?? "none",
                mediaShape = trimmedMediaBody.StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase) ? "hls" : trimmedMediaBody.StartsWith('<') ? "html" : "other"
            }));
            if (trimmedMediaBody.StartsWith('<'))
            {
                var parserTasks = profile.Parses.Where(parser => parser.Type == 0 && !string.IsNullOrWhiteSpace(parser.Url)).Select(async (parser, index) =>
                {
                    try
                    {
                        var parserAddress = parser.Url + Uri.EscapeDataString(request.Url);
                        using var parserResponse = await httpClient.GetAsync(parserAddress);
                        var parserBody = (await parserResponse.Content.ReadAsStringAsync()).TrimStart();
                        var hasUrl = false;
                        if (parserBody.StartsWith('{'))
                        {
                            using var parserJson = JsonDocument.Parse(parserBody);
                            hasUrl = parserJson.RootElement.TryGetProperty("url", out var value) && value.ValueKind == JsonValueKind.String;
                        }
                        return new { index, status = (int)parserResponse.StatusCode, shape = parserBody.StartsWith('{') ? "json" : parserBody.StartsWith('<') ? "html" : "other", hasUrl };
                    }
                    catch (Exception exception)
                    {
                        return new { index, status = 0, shape = exception.GetType().Name, hasUrl = false };
                    }
                });
                foreach (var parserResult in await Task.WhenAll(parserTasks)) Console.WriteLine(JsonSerializer.Serialize(new { parserResult }));
            }
            using var playback = new NativePlaybackService();
            await playback.OpenAsync(request);
            await Task.Delay(TimeSpan.FromSeconds(8));
            Console.WriteLine(JsonSerializer.Serialize(new { playbackState = playback.Player.State.ToString(), playback.Player.IsPlaying }));
            break;
        }
    }
    catch (Exception exception)
    {
        Console.WriteLine(JsonSerializer.Serialize(new { site = site.Name, site.Type, error = exception.GetType().Name }));
    }
}
