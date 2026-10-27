using System.Text;
using JableLens.Core.Http;
using JableLens.Core.Models;
using JableLens.Core.Names;
using JableLens.Core.Sources.Av01;
using JableLens.Core.Sources.Netflav;
using JableLens.Core.Sources.R18;
using JableLens.Core.Subtitles;
using JableLens.Core.Text;

Console.OutputEncoding = Encoding.UTF8;
Console.InputEncoding = Encoding.UTF8;

if (args.Length == 0)
{
    Console.WriteLine("""
        JableLens 数据层试用工具

          works   <演员名> [页码]        Netflav 演员资料 + 作品列表（名字要用 Netflav 写法）
          code    <番号>                 按番号查 AV01 / Netflav / R18.dev
          subs    <番号> [语言码]         搜字幕、列出语言、下载并显示前几句（默认 zh-CN）
          resolve <Jable演员名> [番号]    演员名自动对应到 Netflav 写法
          actors  <关键词>               Netflav 演员搜索
          dir     [censored|uncensored] [age=1-6] [height=1-6] [breast=1-11] [cup=A-M]
          s2t     <文字>                 简繁互转
        """);
    return 0;
}

using var http = HttpClients.Create();
var netflav = new NetflavClient(http);
var av01 = new Av01Client(http);
var r18 = new R18Client(http);
var subs = new SubtitleCatClient(http);

try
{
    switch (args[0])
    {
        case "works": await Works(args[1], args.Length > 2 ? int.Parse(args[2]) : 1); break;
        case "code": await Code(args[1]); break;
        case "subs": await Subs(args[1], args.Length > 2 ? args[2] : "zh-CN"); break;
        case "resolve": await Resolve(args[1], args.Length > 2 ? args[2] : null); break;
        case "actors": PrintPerformers(await netflav.SearchActressAsync(args[1])); break;
        case "dir": PrintPerformers(await netflav.GetActressDirectoryAsync(ParseQuery(args.Skip(1)))); break;
        case "s2t":
            Console.WriteLine($"简体：{ChineseConverter.ToSimplified(args[1])}");
            Console.WriteLine($"繁体：{ChineseConverter.ToTraditional(args[1])}");
            break;
        default: Console.WriteLine($"不认识的命令：{args[0]}"); return 1;
    }
    return 0;
}
catch (SourceException e)
{
    Console.WriteLine($"[读取失败] {e.Message}");
    return 2;
}
catch (IndexOutOfRangeException)
{
    Console.WriteLine("参数不够，直接运行不带参数可以看用法。");
    return 1;
}

async Task Works(string name, int page)
{
    var p = await netflav.GetActressPageAsync(name, page);
    if (p.Profile is { } a)
    {
        Console.WriteLine($"演员：{a.Name}  日文：{a.NameJa}  中文：{a.NameZh}");
        Console.WriteLine($"生日：{a.Birthday?.ToString() ?? "—"}  年龄：{a.AgeOn(DateOnly.FromDateTime(DateTime.Today))?.ToString() ?? "—"}  身高：{a.Height?.ToString() ?? "—"}  罩杯：{a.Cup ?? "—"}  三围：{a.Bust}/{a.Waist}/{a.Hip}  作品数：{a.VideoCount}");
    }
    else
    {
        Console.WriteLine("（Netflav 没有该演员资料）");
    }
    Console.WriteLine($"第 {page} 页：{p.Works.Items.Count} 条（站点自报共 {p.Works.ReportedTotal} 条 / {p.Works.ReportedPages} 页，仅供参考）");
    foreach (var w in p.Works.Items)
        Console.WriteLine($"  {w.Code,-12} {w.Date}  {Trim(w.Title, 40)}  [{string.Join("、", w.Actors.Take(3))}{(w.Actors.Count > 3 ? $" 等{w.Actors.Count}人" : "")}]");
    if (!p.Works.HasMore) Console.WriteLine("（本页为空 = 已到最后一页）");
}

async Task Code(string code)
{
    var av = await av01.FindByCodeAsync(code);
    Console.WriteLine("== AV01 ==");
    if (av is null) Console.WriteLine("  没找到");
    else
    {
        Console.WriteLine($"  {av.Code}  发行：{av.PublishedDate}  片长：{av.DurationSeconds / 60} 分  厂牌：{av.Maker}  DMM：{av.DmmId}");
        Console.WriteLine($"  标题：{av.TitleCn ?? av.TitleJa}");
        foreach (var a in av.Actresses)
            Console.WriteLine($"  演员：{a.Name}  （{string.Join(" / ", a.Translations.Where(t => t.Key is "cn" or "tw" or "en").Select(t => $"{t.Key}:{t.Value}"))}）");
        Console.WriteLine($"  标签：{string.Join("、", av.Tags)}");
    }

    Console.WriteLine("== Netflav ==");
    var nf = await netflav.SearchByCodeAsync(code);
    if (nf.Count == 0) Console.WriteLine("  没找到");
    foreach (var w in nf)
        Console.WriteLine($"  {w.Code}  {w.Date}  演员：{string.Join("、", w.Actors)}\n  封面：{w.CoverUrl}");

    Console.WriteLine("== R18.dev ==");
    Console.WriteLine($"  content_id 候选：{string.Join(", ", VideoCode.DmmContentIdCandidates(code))}");
    if (av?.PublishedDate is not { } date)
    {
        Console.WriteLine("  没有已知发行日，无法核对，跳过（防止张冠李戴）");
        return;
    }
    var d = await r18.LookupAsync(code, date);
    if (d is null) Console.WriteLine($"  没有发行日为 {date} 的资料");
    else
    {
        Console.WriteLine($"  {d.ContentId}  {d.ReleaseDate}  {d.RuntimeMinutes} 分  厂牌：{d.Maker} / {d.Label}");
        Console.WriteLine($"  日文标题：{d.TitleJa}");
        Console.WriteLine($"  演员：{string.Join("、", d.Actresses.Select(a => $"{a.NameKanji}（{a.NameRomaji}）"))}  导演：{string.Join("、", d.Directors.Select(x => x.NameKanji))}");
        Console.WriteLine($"  分类：{string.Join("、", d.Categories)}");
        Console.WriteLine($"  封面：{d.JacketFullUrl}  剧照 {d.Gallery.Count} 张");
    }
}

async Task Subs(string code, string lang)
{
    var hits = await subs.SearchAsync(code);
    if (hits.Count == 0) { Console.WriteLine("没有找到标题含该番号的字幕"); return; }
    SubtitleTrack? chosen = null;
    foreach (var h in hits)
    {
        Console.WriteLine($"字幕页：{h.Title}  {h.PageUrl}");
        var tracks = await subs.GetTracksAsync(h);
        Console.WriteLine($"  {tracks.Count} 种语言：{string.Join("、", tracks.Select(t => $"{t.DisplayName}({t.LanguageCode})"))}");
        chosen ??= tracks.FirstOrDefault(t => t.LanguageCode.Equals(lang, StringComparison.OrdinalIgnoreCase));
    }
    if (chosen is null) { Console.WriteLine($"没有语言 {lang}"); return; }
    var cues = await subs.DownloadAsync(chosen);
    Console.WriteLine($"已下载 {chosen.DisplayName}：{cues.Count} 句，第一句在 {cues.FirstOrDefault()?.Start}");
    foreach (var c in cues.Take(5))
        Console.WriteLine($"  {c.Start:hh\\:mm\\:ss\\.fff} → {c.End:hh\\:mm\\:ss\\.fff}  {c.Text.Replace('\n', ' ')}");
    var offset = SubtitleTiming.OffsetToAlignFirstCue(cues, TimeSpan.FromSeconds(60));
    Console.WriteLine($"对时示例：若在 00:01:00 听到第一句，偏移 = {offset}");
}

async Task Resolve(string name, string? code)
{
    var resolver = new PerformerNameResolver(netflav, av01, PerformerNameResolver.DefaultStore());
    var m = await resolver.ResolveAsync(name, code);
    Console.WriteLine(m is null
        ? $"「{name}」没能对应到 Netflav 写法{(code is null ? "（提供一个该演员的番号会更容易对上）" : "")}"
        : $"「{name}」→「{m.NetflavName}」  方式：{m.Method}");
}

void PrintPerformers(Page<PerformerSummary> p)
{
    if (p.Items.Count == 0) Console.WriteLine("（没有结果）");
    foreach (var a in p.Items)
        Console.WriteLine($"  {a.Name,-12} {a.NameEn,-20} 作品 {a.VideoCount}");
}

static PerformerQuery ParseQuery(IEnumerable<string> args)
{
    var q = new PerformerQuery();
    foreach (var a in args)
    {
        var kv = a.Split('=', 2);
        q = kv[0] switch
        {
            "uncensored" => q with { Type = CensorType.Uncensored },
            "censored" => q with { Type = CensorType.Censored },
            "age" => q with { Age = int.Parse(kv[1]) },
            "height" => q with { Height = int.Parse(kv[1]) },
            "breast" => q with { Breast = int.Parse(kv[1]) },
            "cup" => q with { Cup = kv[1].ToUpperInvariant() },
            _ => q,
        };
    }
    return q;
}

static string Trim(string s, int n) => s.Length <= n ? s : s[..n] + "…";
