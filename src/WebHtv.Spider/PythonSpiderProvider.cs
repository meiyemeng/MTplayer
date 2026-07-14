using System.Diagnostics;
using System.Text.Json;
using WebHtv.Catalogue;
using WebHtv.Core.Catalogue;
using WebHtv.Core.Configuration;

namespace WebHtv.Spider;

/// <summary>Delegates trusted Python spiders to the isolated WebHtv.PythonHost process.</summary>
public sealed class PythonSpiderProvider(HttpClient httpClient, string helperPath) : ITvBoxCatalogueProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    private readonly string _helperPath = helperPath ?? throw new ArgumentNullException(nameof(helperPath));

    public bool CanHandle(TvBoxSite site) => site.Type == 3 && site.Api.EndsWith(".py", StringComparison.OrdinalIgnoreCase) && File.Exists(_helperPath);

    public async Task<CataloguePage> SearchAsync(TvBoxSite site, string keyword, int page, CancellationToken cancellationToken = default)
    {
        var result = await InvokeAsync(site, "search", [keyword, false, page.ToString(System.Globalization.CultureInfo.InvariantCulture)], cancellationToken);
        return TvBoxJsonResultParser.ParsePage(site.Key, result);
    }

    public async Task<CatalogueDetail> GetDetailAsync(TvBoxSite site, string id, CancellationToken cancellationToken = default)
    {
        var result = await InvokeAsync(site, "detail", [id], cancellationToken);
        return TvBoxJsonResultParser.ParseDetail(site.Key, result);
    }

    public PlayRequest CreatePlayRequest(TvBoxSite site, EpisodeSource source, Episode episode) =>
        new(episode.Url, source.Name, true, new Dictionary<string, string>());

    private async Task<string> InvokeAsync(TvBoxSite site, string method, object[] arguments, CancellationToken cancellationToken)
    {
        if (!CanHandle(site)) throw new NotSupportedException("Python Spider 辅助进程不可用。");
        var script = await _httpClient.GetStringAsync(site.Api, cancellationToken);
        var startInfo = new ProcessStartInfo(_helperPath)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动 Python Spider 辅助进程。");
        var request = JsonSerializer.Serialize(new { script, method, arguments }, JsonOptions);
        await process.StandardInput.WriteLineAsync(request);
        process.StandardInput.Close();
        var responseLine = await process.StandardOutput.ReadLineAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var response = JsonSerializer.Deserialize<PythonResponse>(responseLine ?? string.Empty, JsonOptions);
        if (response is null || !string.IsNullOrWhiteSpace(response.Error)) throw new InvalidDataException(response?.Error ?? "Python Spider 未返回结果。");
        return response.ResultJson ?? throw new InvalidDataException("Python Spider 未返回 JSON 结果。");
    }

    private sealed record PythonResponse(string? ResultJson, string? Error);
}
