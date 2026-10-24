using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CraftBox.Core;
using Xunit;

public class ReleaseInfoTests
{
    [Theory]
    [InlineData("Some.Show.S02E05.1080p.WEB.h264-GRP", "Some Show", 2, 5, "1080p")]
    [InlineData("Some Show (2019) - S1E10 - Title [720p]", "Some Show", 1, 10, "720p")]
    [InlineData("Another_Show_3x07_HDTV", "Another Show", 3, 7, "")]
    [InlineData("[Group] Some Show S03 1080p WEB-DL", "Some Show", 3, -1, "1080p")]
    [InlineData("Some.Show.Season.4.Complete.720p", "Some Show", 4, -1, "720p")]
    public void Series(string name, string title, int season, int episode, string quality)
    {
        var i = ReleaseInfo.Parse(name);
        Assert.Equal(title, i.Title);
        Assert.Equal(season, i.Season);
        Assert.Equal(episode, i.Episode);
        Assert.Equal(quality, i.Quality);
    }

    [Theory]
    [InlineData("A.Film.Title.2010.2160p.UHD.BluRay.x265-GRP", "A Film Title", 2010, "2160p", true)]
    [InlineData("A Film Title (1968) 1080p", "A Film Title", 1968, "1080p", false)]
    [InlineData("Film.2049.2017.1080p", "Film 2049", 2017, "1080p", false)]
    public void Movies(string name, string title, int year, string quality, bool hevc)
    {
        var i = ReleaseInfo.Parse(name);
        Assert.False(i.IsSeries);
        Assert.Equal(title, i.Title);
        Assert.Equal(year, i.Year);
        Assert.Equal(quality, i.Quality);
        Assert.Equal(hevc, i.Hevc);
    }

    [Fact]
    public void ShowYearIsSplitOffTheTitle()
    {
        var i = ReleaseInfo.Parse("Some.Show.2019.S01E01.720p");
        Assert.Equal("Some Show", i.Title);
        Assert.Equal(2019, i.Year);
    }

    [Fact]
    public void FlagsDts() => Assert.True(ReleaseInfo.Parse("Film.2001.1080p.BluRay.DTS-HD.MA").Dts);

    [Fact]
    public void KeyIgnoresPunctuationCaseAccentsAndLeadingThe()
    {
        Assert.Equal(ReleaseInfo.Key("The Café: Show!"), ReleaseInfo.Key("cafe show"));
    }
}

public class TorznabTests
{
    public const string Feed = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<rss version=""2.0"" xmlns:torznab=""http://torznab.com/schemas/2015/feed"">
<channel>
<item>
  <title>Some.Show.S01E01.1080p.WEB</title>
  <prowlarrindexer>Home</prowlarrindexer>
  <link>http://indexer/dl/1</link>
  <size>1500000000</size>
  <pubDate>Tue, 20 Oct 2026 10:00:00 +0000</pubDate>
  <enclosure url=""http://indexer/dl/1"" length=""1500000000"" type=""application/x-bittorrent"" />
  <torznab:attr name=""category"" value=""5000"" />
  <torznab:attr name=""category"" value=""5040"" />
  <torznab:attr name=""seeders"" value=""42"" />
  <torznab:attr name=""peers"" value=""50"" />
  <torznab:attr name=""imdbid"" value=""1234567"" />
</item>
<item>
  <title>Some.Show.S01E01.720p.HDTV</title>
  <link>http://indexer/dl/2</link>
  <torznab:attr name=""magneturl"" value=""magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567"" />
  <torznab:attr name=""seeders"" value=""3"" />
  <torznab:attr name=""category"" value=""5030"" />
</item>
<item>
  <title>Some.Show.S01E02.1080p.WEB.DTS</title>
  <link>http://indexer/dl/3</link>
  <torznab:attr name=""seeders"" value=""100"" />
  <torznab:attr name=""category"" value=""5040"" />
</item>
<item>
  <title>Some.Show.S01E02.720p.WEB</title>
  <link>http://indexer/dl/4</link>
  <torznab:attr name=""seeders"" value=""10"" />
  <torznab:attr name=""category"" value=""5030"" />
</item>
<item>
  <title>A.Film.Title.2010.1080p.BluRay</title>
  <link>http://indexer/dl/5</link>
  <torznab:attr name=""seeders"" value=""7"" />
  <torznab:attr name=""category"" value=""2040"" />
  <torznab:attr name=""tmdbid"" value=""27205"" />
</item>
<item><title>no link item</title></item>
</channel>
</rss>";

    [Fact]
    public void ParsesItemsAttributesAndMagnets()
    {
        var r = TorznabClient.Parse(Feed, "fallback");
        Assert.Equal(5, r.Count);
        Assert.Equal("Home", r[0].Indexer);
        Assert.Equal("fallback", r[1].Indexer);
        Assert.Equal(1500000000L, r[0].Size);
        Assert.Equal(42, r[0].Seeders);
        Assert.Equal("tt1234567", r[0].ImdbId);
        Assert.Equal(new[] { 5000, 5040 }, r[0].Categories);
        Assert.StartsWith("magnet:", r[1].Link);
        Assert.Equal(27205, r[4].TmdbId);
        Assert.Equal(new DateTime(2026, 10, 20, 10, 0, 0, DateTimeKind.Utc), r[0].Published.ToUniversalTime());
    }

    [Fact]
    public void IndexerErrorBecomesException()
    {
        var e = Assert.Throws<Exception>(() => TorznabClient.Parse(@"<error code=""100"" description=""Invalid API Key""/>", "Home"));
        Assert.Contains("Invalid API Key", e.Message);
    }
}

public class CatalogTests
{
    [Fact]
    public void GroupsShowsIntoSeasonsAndEpisodesAndRanksReleases()
    {
        var titles = Catalog.Build(TorznabClient.Parse(TorznabTests.Feed, "x"));
        Assert.Equal(2, titles.Count);
        var show = titles.Single(t => t.Kind == TitleKind.Show);
        Assert.Equal("Some Show", show.Name);
        Assert.Equal(new[] { 1 }, show.Seasons);
        var eps = show.Episodes(1);
        Assert.Equal(new[] { 1, 2 }, eps.Keys);
        Assert.Equal("Some.Show.S01E01.1080p.WEB", eps[1][0].Title);      // more seeders first
        Assert.Equal("Some.Show.S01E02.720p.WEB", eps[2][0].Title);       // DTS sorts last despite 100 seeders
        var movie = titles.Single(t => t.Kind == TitleKind.Movie);
        Assert.Equal(2010, movie.Year);
        Assert.Equal(27205, movie.TmdbId);
    }
}

public class PickFileTests
{
    [Fact]
    public void PicksEpisodeFromPackAndSkipsSamples()
    {
        var files = new List<(string, long)>
        {
            ("Show.S01/Show.S01E01.mkv", 900), ("Show.S01/Show.S01E02.mkv", 800),
            ("Show.S01/Show.S01E12.mkv", 950), ("Show.S01/sample/Show.S01E02.sample.mkv", 10),
            ("Show.S01/info.nfo", 1)
        };
        Assert.Equal(1, StreamEngine.PickIndex(files, 1, 2));
        Assert.Equal(0, StreamEngine.PickIndex(files, 1, 1));   // E1 must not match E12
        Assert.Equal(2, StreamEngine.PickIndex(files, -1, -1)); // movie: largest video
        Assert.Equal(-1, StreamEngine.PickIndex(new List<(string, long)> { ("a.nfo", 1) }, -1, -1));
    }
}

/// Real network: fake Torznab indexer on localhost → search → catalog → stream an
/// open-licence film (Big Buck Bunny, CC-BY) through the engine and read bytes back.
/// Opt-in: TVT_NET=1 dotnet test
public class EndToEndTests
{
    const string VideoMagnet = "magnet:?xt=urn:btih:dd8255ecdc7ca55fb0bbf81323d87062db1f6d1c&dn=Big+Buck+Bunny"
        + "&tr=udp%3A%2F%2Ftracker.opentrackr.org%3A1337%2Fannounce&tr=udp%3A%2F%2Fexodus.desync.com%3A6969%2Fannounce&tr=udp%3A%2F%2Ftracker.torrent.eu.org%3A451%2Fannounce";

    [Fact]
    public async Task IndexerRedirectToMagnetStreamsBytes()
    {
        if (Environment.GetEnvironmentVariable("TVT_NET") != "1") return;

        var listener = new HttpListener();
        var port = new Random().Next(20000, 40000);
        var prefix = $"http://127.0.0.1:{port}/";
        listener.Prefixes.Add(prefix);
        listener.Start();
        _ = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync(); } catch { return; }
                if (ctx.Request.Url.AbsolutePath == "/api")
                {
                    Assert.Equal("secret", ctx.Request.QueryString["apikey"]);
                    var xml = $@"<rss xmlns:torznab=""http://torznab.com/schemas/2015/feed""><channel><item>
<title>Big.Buck.Bunny.2008.1080p</title><link>{prefix}dl</link><torznab:attr name=""seeders"" value=""1000""/></item></channel></rss>";
                    var b = Encoding.UTF8.GetBytes(xml);
                    ctx.Response.ContentType = "application/rss+xml";
                    await ctx.Response.OutputStream.WriteAsync(b);
                }
                else
                {
                    ctx.Response.StatusCode = 302;   // what Prowlarr does for magnet-only results
                    ctx.Response.RedirectLocation = VideoMagnet;
                }
                ctx.Response.Close();
            }
        });

        var http = new HttpClient();
        var ix = new IndexerConfig { Name = "fake", Url = prefix + "api", ApiKey = "secret" };
        var (results, errors) = await new TorznabClient(http).SearchAllAsync(new[] { ix }, "bunny", 0);
        Assert.Empty(errors);
        var title = Assert.Single(Catalog.Build(results));

        var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tvt-e2e-" + Guid.NewGuid().ToString("N"));
        using (var engine = await StreamEngine.CreateAsync(tmp))
        using (var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3)))
        {
            var session = await engine.OpenAsync(title.Releases[0].Link, -1, -1, 10L << 30, null, null, cts.Token);
            var req = new HttpRequestMessage(HttpMethod.Get, session.Uri);
            req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, (2 << 20) - 1);
            var res = await http.SendAsync(req, cts.Token);
            var bytes = await res.Content.ReadAsByteArrayAsync();
            Assert.Equal(206, (int)res.StatusCode);
            Assert.Equal(2 << 20, bytes.Length);
            Assert.Equal("ftyp", Encoding.ASCII.GetString(bytes, 4, 4));   // MP4 header: the real file, in order
            Assert.True(System.IO.Directory.Exists(session.Dir));
            await engine.CloseAsync(session);
            Assert.False(System.IO.Directory.Exists(session.Dir));
            await engine.CloseAsync(session);   // second close is a no-op
        }
        listener.Stop();
        System.IO.Directory.Delete(tmp, true);
    }
}

public class ConfigServerTests
{
    [Fact]
    public async Task PostSavesIndexersAndPageEscapesValues()
    {
        var current = new Settings();
        var port = new Random().Next(40000, 50000);
        using (var server = new ConfigServer(port, () => current, s => current = s))
        {
            server.Start();
            var http = new HttpClient();
            var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["name0"] = "Home <Prowlarr>", ["url0"] = "http://nas:9696/1/api", ["key0"] = "abc",
                ["url1"] = "", ["url2"] = "http://nas:9117/api", ["tmdb"] = "k", ["buffer"] = "1500"
            });
            var res = await http.PostAsync($"http://127.0.0.1:{port}/", form);
            var html = await res.Content.ReadAsStringAsync();
            Assert.Equal(2, current.Indexers.Count);
            Assert.Equal("Home <Prowlarr>", current.Indexers[0].Name);
            Assert.Equal("Indexer 3", current.Indexers[1].Name);
            Assert.Equal(1500, current.BufferLimitMb);
            Assert.Contains("Home &lt;Prowlarr&gt;", html);
            Assert.DoesNotContain("Home <Prowlarr>", html);
        }
    }

    [Fact]
    public void SettingsRoundTripThroughFile()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tvt-settings-" + Guid.NewGuid().ToString("N"), "s.json");
        var s = new Settings { TmdbKey = "t" };
        s.Indexers.Add(new IndexerConfig { Name = "a", Url = "http://x/api", ApiKey = "k" });
        s.Save(path);
        var back = Settings.Load(path);
        Assert.Equal("http://x/api", back.Indexers.Single().Url);
        Assert.Equal("t", back.TmdbKey);
        Assert.Equal(3000, Settings.Load(path + ".missing").BufferLimitMb);
        System.IO.Directory.Delete(System.IO.Path.GetDirectoryName(path), true);
    }
}
