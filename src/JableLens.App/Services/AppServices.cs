using System.Net.Http;
using JableLens.Core.Http;
using JableLens.Core.Library;
using JableLens.Core.Names;
using JableLens.Core.Sources.Av01;
using JableLens.Core.Sources.Netflav;
using JableLens.Core.Sources.R18;
using JableLens.Core.Subtitles;
using Microsoft.Web.WebView2.Wpf;

namespace JableLens.App.Services;

/// <summary>全局服务（单窗口应用，用静态容器足够）。</summary>
public static class AppServices
{
    public static HttpClient Http { get; } = HttpClients.Create();
    public static NetflavClient Netflav { get; } = new(Http);
    public static Av01Client Av01 { get; } = new(Http);
    public static R18Client R18 { get; } = new(Http);
    public static SubtitleCatClient Subtitles { get; } = new(Http);
    public static PerformerNameResolver Names { get; } = new(Netflav, Av01, PerformerNameResolver.DefaultStore());

    public static UserLibrary Library { get; private set; } = null!;
    public static WebPageReader JableReader { get; private set; } = null!;
    public static SiteClient Sites { get; private set; } = null!;
    public static RecommendationService Recommend { get; private set; } = null!;
    public static Downloader Downloads { get; private set; } = null!;
    public static NewWorkChecker Checker { get; private set; } = null!;

    public static async Task InitAsync(WebView2 jableView, WebView2 downloadView)
    {
        Downloads = new Downloader(downloadView);
        Library = await UserLibrary.LoadAsync();
        JableReader = new WebPageReader(jableView, "Jable");
        Sites = new SiteClient(JableReader);
        Recommend = new RecommendationService(Sites, Library);
        if (Perf.Enabled) SiteClient.Log = Perf.Log;
        Checker = new NewWorkChecker(Netflav, Library);
        ImageLoader.Http = Http;
        _ = Task.Run(() => ImageLoader.TrimDiskCache());
    }
}
