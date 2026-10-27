using System.Net;
using System.Text;
using JableLens.Core.Names;
using JableLens.Core.Sources.Av01;
using JableLens.Core.Sources.Netflav;

namespace JableLens.Core.Tests;

public class NameResolverTests
{
    /// <summary>按 URL 片段返回固定内容的假网络。</summary>
    private sealed class FakeHandler(Func<HttpRequestMessage, string?> respond) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!.ToString());
            var body = respond(request);
            return Task.FromResult(body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private const string EmptyActressSearch = """{"result":{"docs":[],"total":0,"page":"1","pages":1}}""";

    private static (PerformerNameResolver, FakeHandler) Create(Func<HttpRequestMessage, string?> respond)
    {
        var fake = new FakeHandler(respond);
        var http = new HttpClient(fake);
        return (new PerformerNameResolver(new NetflavClient(http), new Av01Client(http)), fake);
    }

    [Fact]
    public async Task ExactNetflavSearchHit_IsUsed()
    {
        var (r, _) = Create(req => req.RequestUri!.AbsolutePath.Contains("searchActress")
            ? """{"result":{"docs":[{"_id":"1","name":"小野六花","name_en":"Rikka Ono","videoCount":1},{"_id":"2","name":"小野六花2"}]}}"""
            : null);
        var m = await r.ResolveAsync("小野六花");
        Assert.Equal("小野六花", m?.NetflavName);
        Assert.Contains("精确", m!.Method);
    }

    [Fact]
    public async Task FallsBackToAv01ByCode_ForSingleActressWork()
    {
        var (r, _) = Create(req =>
            req.RequestUri!.AbsolutePath.Contains("searchActress") ? EmptyActressSearch
            : req.RequestUri.Host.Contains("av01") ? ParserTests.Fixture("av01_search.json")
            : null);
        var m = await r.ResolveAsync("和香奈月", "MFYD-202");
        Assert.Equal("和香なつき", m?.NetflavName);
        Assert.Contains("AV01", m!.Method);
    }

    [Fact]
    public async Task MultiActressWork_IsNotGuessed()
    {
        var (r, _) = Create(req =>
            req.RequestUri!.AbsolutePath.Contains("searchActress") ? EmptyActressSearch
            : req.RequestUri.Host.Contains("av01") ? ParserTests.Fixture("av01_search.json")
            : req.RequestUri.AbsolutePath.Contains("advanceSearchVideo") ? ParserTests.Fixture("nf_code.json")
            : null);
        Assert.Null(await r.ResolveAsync("和香奈月", "MFYD-202", actorsInWork: 2));
    }

    [Fact]
    public async Task SourceFailure_DoesNotThrow_AndResultIsCached()
    {
        var (r, fake) = Create(req =>
            req.RequestUri!.AbsolutePath.Contains("searchActress") ? EmptyActressSearch
            : req.RequestUri.Host.Contains("av01") ? null // AV01 404
            : req.RequestUri.AbsolutePath.Contains("advanceSearchVideo") ? ParserTests.Fixture("nf_code.json")
            : null);
        var m = await r.ResolveAsync("和香奈月", "MFYD-202");
        Assert.Equal("和香なつき", m?.NetflavName);
        Assert.Contains("Netflav 番号反查", m!.Method);

        var before = fake.Requests.Count;
        var again = await r.ResolveAsync("和香奈月", "MFYD-202");
        Assert.StartsWith("缓存", again!.Method);
        Assert.Equal(before, fake.Requests.Count);
    }
}
