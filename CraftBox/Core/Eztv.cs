using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CraftBox.Core
{
    public class EztvClient
    {
        readonly HttpClient http;

        public EztvClient(HttpClient http) { this.http = http; }

        public async Task<List<Release>> BrowseAsync(string baseUrl, int page = 1, int limit = 100, CancellationToken ct = default)
        {
            var url = baseUrl.TrimEnd('/') + $"?page={page}&limit={limit}";
            return await FetchAsync(url, ct).ConfigureAwait(false);
        }

        public async Task<List<Release>> SearchByImdbAsync(string baseUrl, string imdbId, int limit = 100, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(imdbId)) return new List<Release>();
            var id = imdbId.StartsWith("tt") ? imdbId.Substring(2) : imdbId;
            var all = new List<Release>();
            for (int page = 1; page <= 20; page++)
            {
                var url = baseUrl.TrimEnd('/') + $"?imdb_id={id}&limit={limit}&page={page}";
                var batch = await FetchAsync(url, ct).ConfigureAwait(false);
                if (batch.Count == 0) break;
                all.AddRange(batch);
                if (batch.Count < limit) break;
            }
            return all;
        }

        async Task<List<Release>> FetchAsync(string url, CancellationToken ct)
        {
            try
            {
                using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    cts.CancelAfter(TimeSpan.FromSeconds(8));
                    using (var res = await http.GetAsync(url, cts.Token).ConfigureAwait(false))
                    {
                        var body = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
                        if (!res.IsSuccessStatusCode)
                            throw new Exception($"EZTV {(int)res.StatusCode}");
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
                if (!doc.RootElement.TryGetProperty("torrents", out var arr))
                    return list;
                foreach (var t in arr.EnumerateArray())
                {
                    var magnet = Str(t, "magnet_url");
                    var title = Str(t, "title");
                    if (string.IsNullOrEmpty(magnet) || string.IsNullOrEmpty(title))
                        continue;
                    var screenshot = Str(t, "small_screenshot");
                    if (string.IsNullOrEmpty(screenshot)) screenshot = Str(t, "large_screenshot");
                    var r = new Release
                    {
                        Title = title,
                        Indexer = "EZTV",
                        Link = magnet,
                        Size = Long(t, "size_bytes"),
                        Seeders = (int)Long(t, "seeds"),
                        Peers = (int)Long(t, "peers"),
                        ImdbId = NormalizeImdb(Str(t, "imdb_id")),
                        PosterUrl = screenshot,
                    };
                    var epoch = Long(t, "date_released_unix");
                    if (epoch > 0)
                        r.Published = DateTimeOffset.FromUnixTimeSeconds(epoch).UtcDateTime;
                    r.Categories.Add(Cat.Tv);
                    r.Info = ReleaseInfo.Parse(r.Title);
                    list.Add(r);
                }
            }
            return list;
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

        static string NormalizeImdb(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            s = s.Trim();
            if (s.StartsWith("tt")) return s;
            return long.TryParse(s, out var n) && n > 0 ? "tt" + n.ToString("D7") : null;
        }
    }
}
