using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CraftBox.Core
{
    public class YtsClient
    {
        readonly HttpClient http;

        static readonly string[] Trackers =
        {
            "udp://open.demonii.com:1337/announce",
            "udp://tracker.openbittorrent.com:80",
            "udp://tracker.coppersurfer.tk:6969",
            "udp://glotorrents.pw:6969/announce",
            "udp://tracker.opentrackr.org:1337/announce",
            "udp://torrent.gresille.org:80/announce",
            "udp://p4p.arenabg.com:1337",
            "udp://tracker.leechers-paradise.org:6969",
        };

        public YtsClient(HttpClient http) { this.http = http; }

        public async Task<List<Release>> BrowseAsync(string baseUrl, int page = 1, int limit = 50,
            string sortBy = "date_added", string quality = null, CancellationToken ct = default)
        {
            var url = baseUrl.TrimEnd('/') + $"?page={page}&limit={limit}&sort_by={sortBy}";
            if (!string.IsNullOrEmpty(quality)) url += $"&quality={quality}";
            return await FetchAsync(url, ct).ConfigureAwait(false);
        }

        public async Task<List<Release>> SearchAsync(string baseUrl, string query, int limit = 50, CancellationToken ct = default)
        {
            var url = baseUrl.TrimEnd('/') + $"?query_term={Uri.EscapeDataString(query)}&limit={limit}&sort_by=seeds";
            return await FetchAsync(url, ct).ConfigureAwait(false);
        }

        async Task<List<Release>> FetchAsync(string url, CancellationToken ct)
        {
            try
            {
                using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    cts.CancelAfter(TimeSpan.FromSeconds(10));
                    using (var res = await http.GetAsync(url, cts.Token).ConfigureAwait(false))
                    {
                        var body = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
                        if (!res.IsSuccessStatusCode)
                            throw new Exception($"YTS {(int)res.StatusCode}");
                        return Parse(body);
                    }
                }
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                return await FetchWithDohAsync(url, ct).ConfigureAwait(false);
            }
        }

        async Task<List<Release>> FetchWithDohAsync(string url, CancellationToken ct)
        {
            var body = await DohResolver.FetchAsync(url, ct).ConfigureAwait(false);
            return Parse(body);
        }

        public static List<Release> Parse(string json)
        {
            var list = new List<Release>();
            using (var doc = JsonDocument.Parse(json))
            {
                if (!doc.RootElement.TryGetProperty("data", out var data)) return list;
                if (!data.TryGetProperty("movies", out var movies)) return list;
                foreach (var m in movies.EnumerateArray())
                {
                    var movieTitle = Str(m, "title_long");
                    if (string.IsNullOrEmpty(movieTitle)) movieTitle = Str(m, "title");
                    var imdbCode = Str(m, "imdb_code");
                    var year = (int)Long(m, "year");
                    var coverUrl = Str(m, "medium_cover_image");
                    if (string.IsNullOrEmpty(coverUrl)) coverUrl = Str(m, "large_cover_image");

                    if (!m.TryGetProperty("torrents", out var torrents)) continue;
                    foreach (var t in torrents.EnumerateArray())
                    {
                        var hash = Str(t, "hash");
                        if (string.IsNullOrEmpty(hash)) continue;

                        var quality = Str(t, "quality");
                        var releaseName = $"{movieTitle} [{quality}] [YTS]";
                        var magnet = BuildMagnet(hash, movieTitle);

                        var r = new Release
                        {
                            Title = releaseName,
                            Indexer = "YTS",
                            Link = magnet,
                            Size = Long(t, "size_bytes"),
                            Seeders = (int)Long(t, "seeds"),
                            Peers = (int)Long(t, "peers"),
                            ImdbId = string.IsNullOrEmpty(imdbCode) ? null : imdbCode,
                            PosterUrl = coverUrl,
                        };
                        var epoch = Long(t, "date_uploaded_unix");
                        if (epoch > 0)
                            r.Published = DateTimeOffset.FromUnixTimeSeconds(epoch).UtcDateTime;
                        r.Categories.Add(Cat.Movies);
                        r.Info = ReleaseInfo.Parse(releaseName);
                        if (r.Info.Year == 0) r.Info.Year = year;
                        if (string.IsNullOrEmpty(r.Info.Quality) && !string.IsNullOrEmpty(quality))
                            r.Info.Quality = quality.ToLowerInvariant();
                        list.Add(r);
                    }
                }
            }
            return list;
        }

        static string BuildMagnet(string hash, string name)
        {
            var magnet = $"magnet:?xt=urn:btih:{hash}&dn={Uri.EscapeDataString(name)}";
            foreach (var tr in Trackers)
                magnet += "&tr=" + Uri.EscapeDataString(tr);
            return magnet;
        }

        static string Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : "";

        static long Long(JsonElement e, string name)
        {
            if (!e.TryGetProperty(name, out var v)) return 0;
            if (v.ValueKind == JsonValueKind.Number) return v.GetInt64();
            if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) return n;
            return 0;
        }
    }
}
