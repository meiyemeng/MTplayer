using WebHtv.Configuration;
using WebHtv.Catalogue;

var testDirectory = Path.Combine(Path.GetTempPath(), $"WebHtvConfigurationCheck-{Guid.NewGuid():N}");

try
{
    Directory.CreateDirectory(testDirectory);
    var filePath = Path.Combine(testDirectory, "configuration.json");
    var source = """
        {
          "spider": "https://example.invalid/spider.jar",
          "sites": [
            { "key": "demo", "name": "示例站点", "api": "csp_Demo", "type": 3, "searchable": 1 }
          ],
          "lives": [
            { "name": "默认直播", "url": "https://example.invalid/live.txt", "type": 0 }
          ],
          "parses": [
            { "name": "默认解析", "type": 0, "url": "https://example.invalid/parse?url=" }
          ],
          "futureField": { "isPreserved": true }
        }
        """;
    var document = ConfigurationImporter.Import(source);
    var store = new AtomicFileConfigurationStore(filePath);

    await store.SaveAsync(document);
    var loaded = await store.LoadAsync();

    Require(loaded.SourceText == source, "The saved configuration did not round-trip.");
    Require(!File.Exists($"{filePath}.tmp"), "A temporary configuration file was left behind.");

    var profile = TvBoxProfileParser.Parse(source).Profile;
    Require(profile is not null, "The TVBox configuration was not parsed.");
    if (profile is null)
    {
        throw new InvalidOperationException("The TVBox configuration was not parsed.");
    }

    Require(profile.Sites.Single().Key == "demo", "The TVBox site key was not preserved.");
    Require(profile.Lives.Single().Name == "默认直播", "The TVBox live source was not preserved.");
    Require(profile.Parses.Single().Name == "默认解析", "The TVBox parser was not preserved.");
    Require(profile.ExtensionData.ContainsKey("futureField"), "Unknown TVBox fields were not preserved.");

    var wrappedSource = "jhSPAyzn**" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(source));
    var wrappedDocument = ConfigurationImporter.Import(wrappedSource);
    Require(wrappedDocument.SourceText == source, "Wrapped Base64 configuration was not decoded.");

    var malformedSource = "{ \"name\": \"line one\nline two\", \"sites\": [] }";
    var normalizedDocument = ConfigurationImporter.Import(malformedSource);
    Require(normalizedDocument.SourceText.Contains("\\n", StringComparison.Ordinal), "Unescaped control characters were not normalized.");

    var duplicateKeysProfile = TvBoxProfileParser.Parse("""{ "sites": [{ "key": "same" }, { "key": "same" }] }""").Profile;
    Require(duplicateKeysProfile is not null && duplicateKeysProfile.Sites[1].RuntimeKey == "same#2", "Duplicate site keys were not assigned unique runtime keys.");

    var remoteAddress = Environment.GetEnvironmentVariable("WEBHTV_REMOTE_CONFIGURATION_CHECK");
    if (!string.IsNullOrWhiteSpace(remoteAddress))
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "curl.exe")) { UseShellExecute = false, RedirectStandardOutput = true };
        foreach (var argument in new[] { "--noproxy", "*", "--silent", "--location", "--insecure", remoteAddress }) startInfo.ArgumentList.Add(argument);
        using var remoteProcess = System.Diagnostics.Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to start curl.");
        var remoteSource = await remoteProcess.StandardOutput.ReadToEndAsync();
        await remoteProcess.WaitForExitAsync();
        var remoteDocument = ConfigurationImporter.Import(remoteSource);
        var remoteProfile = TvBoxProfileParser.Parse(remoteDocument.SourceText).Profile!;
        Console.WriteLine($"Remote import: {remoteProfile.Sites.Count} sites, {remoteProfile.Lives.Count} lives, {remoteProfile.Parses.Count} parses.");
    }

    var catalogue = TvBoxJsonResultParser.ParsePage("demo", """
        { "pagecount": 2, "list": [
          { "vod_id": "100", "vod_name": "真实标题", "vod_pic": "https://example.invalid/cover.jpg", "vod_remarks": "第 1 集", "type_name": "电影" }
        ] }
        """);
    Require(catalogue.Items.Single().Title == "真实标题", "The TVBox result title was not parsed.");
    Require(catalogue.PageCount == 2, "The TVBox result page count was not parsed.");

    var detail = TvBoxJsonResultParser.ParseDetail("demo", """
        { "list": [
          { "vod_id": "100", "vod_name": "真实标题", "vod_play_from": "线路一$$$线路二", "vod_play_url": "第一集$https://example.invalid/a.m3u8#第二集$https://example.invalid/b.m3u8$$$正片$https://example.invalid/c.mp4" }
        ] }
        """);
    Require(detail.Sources.Count == 2 && detail.Sources[0].Episodes.Count == 2, "The TVBox episode sources were not parsed.");

    var recordingHandler = new RecordingHandler();
    var provider = new HttpTvBoxCatalogueProvider(new HttpClient(recordingHandler));
    var pageWithExistingAction = await provider.SearchAsync(new WebHtv.Core.Configuration.TvBoxSite
    {
        Key = "existing-action",
        RuntimeKey = "existing-action",
        Name = "Existing action",
        Type = 1,
        Api = "https://example.invalid/api.php/provide/vod/?ac=list&token=preserved"
    }, "电影", 1);
    Require(pageWithExistingAction.Items.Count == 1, "The provider did not parse the replacement-query response.");
    Require(recordingHandler.LastRequestUri is not null, "The provider did not issue a request.");
    Require(recordingHandler.LastRequestUri!.Query.Contains("ac=detail", StringComparison.OrdinalIgnoreCase), "The existing action was not replaced.");
    Require(!recordingHandler.LastRequestUri.Query.Contains("ac=list", StringComparison.OrdinalIgnoreCase), "The obsolete action remained in the query.");
    Require(recordingHandler.LastRequestUri.Query.Contains("token=preserved", StringComparison.Ordinal), "Unrelated query parameters were not preserved.");

    var invalidRejected = false;
    try
    {
        ConfigurationImporter.Import("not json");
    }
    catch (ArgumentException)
    {
        invalidRejected = true;
    }

    Require(invalidRejected, "Invalid JSON was accepted as a configuration.");
    Console.WriteLine("Configuration integration checks passed.");
    return 0;
}
finally
{
    if (Directory.Exists(testDirectory))
    {
        Directory.Delete(testDirectory, recursive: true);
    }
}

static void Require(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

sealed class RecordingHandler : HttpMessageHandler
{
    public Uri? LastRequestUri { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequestUri = request.RequestUri;
        return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent("""{"pagecount":1,"list":[{"vod_id":1,"vod_name":"电影测试"}]}""")
        });
    }
}
