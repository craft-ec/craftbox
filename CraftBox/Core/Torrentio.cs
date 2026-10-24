using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace CraftBox.Core
{
    public class TorrentioClient
    {
        const string Base = "https://torrentio.strem.fun/stream";

        static readonly string[] Trackers =
        {
            "udp://tracker.opentrackr.org:1337/announce",
            "udp://open.stealth.si:80/announce",
            "udp://tracker.torrent.eu.org:451/announce",
            "udp://tracker.dler.org:6969/announce",
            "udp://tracker.openbittorrent.com:6969/announce",
        };

        static readonly Regex SeedersRx = new Regex(@"👤\s*(\d+)");
        static readonly Regex SizeRx = new Regex(@"💾\s*([\d.]+)\s*(GB|MB|TB)", RegexOptions.IgnoreCase);
        static readonly Regex SourceRx = new Regex(@"⚙️\s*(\S+)");

        public async Task<List<Release>> SearchMovieAsync(string imdbId, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(imdbId)) return new List<Release>();
            var url = $"{Base}/movie/{imdbId}.json";
            return await FetchAsync(url, false, ct).ConfigureAwait(false);
        }

        public async Task<List<Release>> SearchEpisodeAsync(string imdbId, int season, int episode, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(imdbId)) return new List<Release>();
            var url = $"{Base}/series/{imdbId}:{season}:{episode}.json";
            return await FetchAsync(url, true, ct).ConfigureAwait(false);
        }

        async Task<List<Release>> FetchAsync(string url, bool tv, CancellationToken ct)
        {
            string json;
            try
            {
                json = await DohResolver.FetchAsync(url, ct).ConfigureAwait(false);
            }
            catch
            {
                using (var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(15) })
                    json = await http.GetStringAsync(url).ConfigureAwait(false);
            }
            return Parse(json, tv);
        }

        static List<Release> Parse(string json, bool tv)
        {
            var list = new List<Release>();
            using (var doc = System.Text.Json.JsonDocument.Parse(json))
            {
                if (!doc.RootElement.TryGetProperty("streams", out var streams)) return list;
                foreach (var s in streams.EnumerateArray())
                {
                    var hash = Str(s, "infoHash");
                    if (string.IsNullOrEmpty(hash)) continue;
                    var title = Str(s, "title");
                    if (string.IsNullOrEmpty(title)) continue;

                    var lines = title.Split('\n');
                    var releaseName = lines[0].Trim();
                    int seeders = 0;
                    long size = 0;
                    string source = "Torrentio";

                    for (int i = 1; i < lines.Length; i++)
                    {
                        var m = SeedersRx.Match(lines[i]);
                        if (m.Success) seeders = int.Parse(m.Groups[1].Value);
                        var sm = SizeRx.Match(lines[i]);
                        if (sm.Success) size = ParseSize(sm.Groups[1].Value, sm.Groups[2].Value);
                        var src = SourceRx.Match(lines[i]);
                        if (src.Success) source = src.Groups[1].Value;
                    }

                    var magnet = BuildMagnet(hash, releaseName);
                    var r = new Release
                    {
                        Title = releaseName,
                        Indexer = source,
                        Link = magnet,
                        Size = size,
                        Seeders = seeders,
                        Peers = 0,
                    };
                    r.Info = ReleaseInfo.Parse(releaseName);
                    if (tv) r.Categories.Add(Cat.Tv);
                    else r.Categories.Add(Cat.Movies);
                    list.Add(r);
                }
            }
            return list;
        }

        static long ParseSize(string num, string unit)
        {
            if (!double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return 0;
            switch (unit.ToUpperInvariant())
            {
                case "TB": return (long)(v * 1024 * 1024 * 1024 * 1024);
                case "GB": return (long)(v * 1024 * 1024 * 1024);
                case "MB": return (long)(v * 1024 * 1024);
                default: return 0;
            }
        }

        static string BuildMagnet(string hash, string name)
        {
            var magnet = $"magnet:?xt=urn:btih:{hash}&dn={Uri.EscapeDataString(name)}";
            foreach (var tr in Trackers)
                magnet += "&tr=" + Uri.EscapeDataString(tr);
            return magnet;
        }

        static string Str(System.Text.Json.JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() : "";
    }
}
