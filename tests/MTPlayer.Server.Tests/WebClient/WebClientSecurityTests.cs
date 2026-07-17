using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Http;
using MTPlayer.Server.WebClient;
using System.Net;
using System.Text;
using Xunit;

namespace MTPlayer.Server.Tests.WebClient;

public sealed class WebClientSecurityTests
{
    private static readonly string Key = Convert.ToBase64String(
        Enumerable.Range(0, 32).Select(index => (byte)index).ToArray());

    [Fact]
    public void Signed_proxy_token_round_trips_only_for_its_resource_kind()
    {
        var signer = CreateSigner();
        var token = signer.Sign("https://media.example/movie/index.m3u8", "media");

        Assert.True(signer.TryRead(token, "media", out var address));
        Assert.Equal("https://media.example/movie/index.m3u8", address?.OriginalString);
        Assert.False(signer.TryRead(token, "image", out _));
    }

    [Fact]
    public void Tampered_or_expired_proxy_token_is_rejected()
    {
        var signer = CreateSigner();
        var token = signer.Sign("https://media.example/video.mp4", "media");
        var separator = token.IndexOf('.');
        var signatureIndex = separator + 2;
        var tampered = token[..signatureIndex] + (token[signatureIndex] == 'A' ? 'B' : 'A') + token[(signatureIndex + 1)..];
        var expired = signer.Sign("https://media.example/video.mp4", "media", TimeSpan.FromSeconds(-1));

        Assert.False(signer.TryRead(tampered, "media", out _));
        Assert.False(signer.TryRead(expired, "media", out _));
        Assert.False(signer.TryRead("not-a-token", "media", out _));
    }

    [Theory]
    [InlineData("https://example.com/video.m3u8", true)]
    [InlineData("http://example.com/video.mp4", true)]
    [InlineData("file:///etc/passwd", false)]
    [InlineData("ftp://example.com/video.mp4", false)]
    public void Only_http_and_https_addresses_are_eligible(string value, bool expected)
    {
        Assert.Equal(expected, WebClientGateway.IsHttp(new Uri(value)));
    }

    [Fact]
    public async Task Configuration_inspection_exposes_only_native_http_catalogue_sites()
    {
        const string config = """
            {
              "sites": [
                { "key": "cms", "name": "可用 CMS", "type": 1, "api": "https://api.example.com/provide/vod/" },
                { "key": "spider", "name": "JS Spider", "type": 3, "api": "https://cdn.example.com/site.js" },
                { "key": "disabled", "name": "禁止搜索", "type": 1, "searchable": 0, "api": "https://api.example.com/disabled" }
              ],
              "lives": [{ "name": "测试直播", "url": "https://live.example.com/index.m3u8" }]
            }
            """;
        var signer = CreateSigner();
        var gateway = new WebClientGateway(new HttpClient(new StaticHandler(config, "application/json")), signer);

        var result = await gateway.InspectAsync(
            new WebConfigRequest(Guid.NewGuid(), "http://93.184.216.34/config.json"),
            CancellationToken.None);

        var site = Assert.Single(result.Sites);
        Assert.Equal("可用 CMS", site.Name);
        Assert.Equal("https://api.example.com/provide/vod/", site.Api);
        Assert.Single(result.Lives);
    }

    [Fact]
    public async Task Hls_proxy_rewrites_relative_segments_to_new_signed_proxy_urls()
    {
        const string manifest = "#EXTM3U\n#EXT-X-KEY:METHOD=AES-128,URI=\"key.bin\"\n#EXTINF:5,\nsegment-1.ts\n";
        var signer = CreateSigner();
        var gateway = new WebClientGateway(new HttpClient(new StaticHandler(manifest, "application/vnd.apple.mpegurl")), signer);
        var token = signer.Sign("http://93.184.216.34/media/master.m3u8", "media");
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        await gateway.ProxyAsync(context, "media", token, CancellationToken.None);
        context.Response.Body.Position = 0;
        var output = await new StreamReader(context.Response.Body).ReadToEndAsync(CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Contains("/api/v1/web/proxy/media?token=", output, StringComparison.Ordinal);
        Assert.DoesNotContain("segment-1.ts\n", output, StringComparison.Ordinal);
        Assert.DoesNotContain("URI=\"key.bin\"", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Configuration_fetch_retries_transient_connection_resets_with_fresh_requests()
    {
        const string config = """
            { "sites": [{ "key": "cms", "name": "CMS", "type": 1,
              "api": "https://api.example.com/provide/vod/" }] }
            """;
        var handler = new ResetThenSuccessHandler(config, failures: 2);
        var gateway = new WebClientGateway(new HttpClient(handler), CreateSigner());

        var result = await gateway.InspectAsync(
            new WebConfigRequest(Guid.NewGuid(), "http://93.184.216.34/config.json"),
            CancellationToken.None);

        Assert.Single(result.Sites);
        Assert.Equal(3, handler.Attempts);
    }

    [Fact]
    public async Task Configuration_inspection_accepts_common_lenient_tvbox_json()
    {
        const string config = """
            {
              // TVBox configurations commonly contain comments.
              "sites": [
                { "key": "cms", "name": "宽
            松 CMS", "type": 1, "api": "https://api.example.com/provide/vod/" },
              ],
            }
            """;
        var gateway = new WebClientGateway(
            new HttpClient(new StaticHandler(config, "application/json")),
            CreateSigner());

        var result = await gateway.InspectAsync(
            new WebConfigRequest(Guid.NewGuid(), "http://93.184.216.34/config.json"),
            CancellationToken.None);

        var site = Assert.Single(result.Sites);
        Assert.Equal("https://api.example.com/provide/vod/", site.Api);
        Assert.Contains("宽", site.Name, StringComparison.Ordinal);
        Assert.Contains("松 CMS", site.Name, StringComparison.Ordinal);
    }

    private static WebProxySigner CreateSigner()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DATA_ENCRYPTION_KEY"] = Key })
            .Build();
        return new WebProxySigner(configuration);
    }

    private sealed class StaticHandler(string content, string contentType) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(content, Encoding.UTF8, contentType),
            RequestMessage = request,
        });
    }

    private sealed class ResetThenSuccessHandler(string content, int failures) : HttpMessageHandler
    {
        public int Attempts { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Attempts++;
            if (Attempts <= failures)
                throw new HttpRequestException("Connection reset by peer");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content, Encoding.UTF8, "application/json"),
                RequestMessage = request,
            });
        }
    }
}
