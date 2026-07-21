using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Media;
using FluentGpu.Media.Adaptive;
using Xunit;

namespace FluentGpu.Engine.Tests;

public sealed class AdaptiveManifestLoaderTests
{
    private const string Mpd = "<MPD xmlns=\"urn:mpeg:dash:schema:mpd:2011\" type=\"static\" mediaPresentationDuration=\"PT2S\"><Period><AdaptationSet contentType=\"video\"><Representation id=\"v\" bandwidth=\"1000\"><SegmentTemplate media=\"$Number$.m4s\" duration=\"2\" timescale=\"1\" startNumber=\"1\" /></Representation></AdaptationSet></Period></MPD>";

    [Fact]
    public async Task Load_DetectsDash_AndMutatesRequest()
    {
        var handler = new ScriptedHandler((request, _) =>
        {
            Assert.Equal("yes", request.Headers.GetValues("X-Test").Single());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Mpd), RequestMessage = request,
            };
        });
        using var client = new HttpClient(handler);
        var source = (AdaptiveSource)MediaSource.FromAdaptive("https://fixture.test/manifest");
        var network = new NetworkOptions(r => { r.Headers.Add("X-Test", "yes"); return r; }, MaxRetries: 0);

        AdaptiveManifest manifest = await AdaptiveManifestLoader.LoadAsync(client, source, network,
            TestContext.Current.CancellationToken);

        Assert.Equal(AdaptiveManifestKind.Dash, manifest.Kind);
        Assert.Single(manifest.TrackGroups);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Load_RetriesTransientHttpFailure_WithinBudget()
    {
        var handler = new ScriptedHandler((request, call) => call == 1
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { RequestMessage = request }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Mpd), RequestMessage = request });
        using var client = new HttpClient(handler);
        var source = (AdaptiveSource)MediaSource.FromAdaptive("https://fixture.test/stream.mpd");

        AdaptiveManifest manifest = await AdaptiveManifestLoader.LoadAsync(client, source,
            new NetworkOptions(MaxRetries: 1), TestContext.Current.CancellationToken);

        Assert.Equal(AdaptiveManifestKind.Dash, manifest.Kind);
        Assert.Equal(2, handler.Calls);
    }

    [Theory]
    [InlineData("https://fixture.test/live", "#EXTM3U\n#EXT-X-TARGETDURATION:2", AdaptiveManifestKind.Hls)]
    [InlineData("https://fixture.test/live.mpd", "not xml", AdaptiveManifestKind.Dash)]
    public void Detect_UsesContentThenExtension(string uri, string body, AdaptiveManifestKind expected)
        => Assert.Equal(expected, AdaptiveManifestLoader.Detect(AdaptiveManifestKind.Auto, new Uri(uri), body));

    private sealed class ScriptedHandler(Func<HttpRequestMessage, int, HttpResponseMessage> send) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(send(request, ++Calls));
    }
}
