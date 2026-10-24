using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace CraftBox.Core
{
    public class Release
    {
        public string Title = "";
        public string Indexer = "";
        public string Link = "";      // magnet: or http(s) URL of a .torrent
        public long Size;
        public int Seeders;
        public int Peers;
        public DateTime Published;
        public string ImdbId;
        public int TmdbId;
        public string PosterUrl;
        public List<int> Categories = new List<int>();
        public ReleaseInfo Info;      // parsed from Title
    }

    /// Standard Torznab categories.
    public static class Cat
    {
        public const int Movies = 2000;
        public const int Tv = 5000;
        public static bool IsMovie(int c) => c >= 2000 && c < 3000;
        public static bool IsTv(int c) => c >= 5000 && c < 6000;
    }

    public class TorznabClient
    {
        static readonly XNamespace Tz = "http://torznab.com/schemas/2015/feed";
        readonly HttpClient http;

        public TorznabClient(HttpClient http) { this.http = http; }

        /// q may be empty: most indexers then return their latest releases, which feeds the home rows.
        public async Task<List<Release>> SearchAsync(IndexerConfig ix, string q, int category, CancellationToken ct = default)
        {
            var url = ix.Url + (ix.Url.Contains("?") ? "&" : "?")
                + "t=search&extended=1&limit=100"
                + "&q=" + Uri.EscapeDataString(q ?? "")
                + (category > 0 ? "&cat=" + category : "")
                + (string.IsNullOrEmpty(ix.ApiKey) ? "" : "&apikey=" + Uri.EscapeDataString(ix.ApiKey));
            using (var res = await http.GetAsync(url, ct).ConfigureAwait(false))
            {
                var body = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!res.IsSuccessStatusCode)
                    throw new Exception($"{ix.Name}: HTTP {(int)res.StatusCode}");
                return Parse(body, ix.Name);
            }
        }

        public async Task<(List<Release> results, List<string> errors)> SearchAllAsync(
            IEnumerable<IndexerConfig> indexers, string q, int category, CancellationToken ct = default)
        {
            var list = indexers.ToList();
            var tasks = list.Select(ix => SearchAsync(ix, q, category, ct)).ToList();
            var results = new List<Release>();
            var errors = new List<string>();
            for (int i = 0; i < tasks.Count; i++)
            {
                try { results.AddRange(await tasks[i].ConfigureAwait(false)); }
                catch (Exception e) when (!(e is OperationCanceledException && ct.IsCancellationRequested))
                {
                    errors.Add(e.Message.StartsWith(list[i].Name) ? e.Message : $"{list[i].Name}: {e.Message}");
                }
            }
            return (results, errors);
        }

        public static List<Release> Parse(string xml, string indexerName)
        {
            var doc = XDocument.Parse(xml);
            var error = doc.Root?.Name.LocalName == "error" ? doc.Root : null;
            if (error != null)
                throw new Exception($"{indexerName}: {(string)error.Attribute("description") ?? "indexer error"}");

            var list = new List<Release>();
            foreach (var item in doc.Descendants("item"))
            {
                var attrs = item.Elements(Tz + "attr")
                    .GroupBy(a => (string)a.Attribute("name"))
                    .ToDictionary(g => g.Key, g => g.Select(a => (string)a.Attribute("value")).ToList());
                string Attr(string name) => attrs.TryGetValue(name, out var v) ? v.FirstOrDefault() : null;

                var r = new Release
                {
                    Title = ((string)item.Element("title") ?? "").Trim(),
                    Indexer = (string)item.Element("jackettindexer") ?? (string)item.Element("prowlarrindexer") ?? indexerName,
                    Link = Attr("magneturl") ?? (string)item.Element("enclosure")?.Attribute("url") ?? (string)item.Element("link") ?? "",
                    Size = ToLong((string)item.Element("size") ?? Attr("size")),
                    Seeders = (int)ToLong(Attr("seeders")),
                    Peers = (int)ToLong(Attr("peers")),
                    ImdbId = NormalizeImdb(Attr("imdbid") ?? Attr("imdb")),
                    TmdbId = (int)ToLong(Attr("tmdbid")),
                };
                r.Published = ParseDate((string)item.Element("pubDate"));
                if (attrs.TryGetValue("category", out var cats))
                    r.Categories.AddRange(cats.Select(c => (int)ToLong(c)).Where(c => c > 0));
                if (string.IsNullOrEmpty(r.Link) || string.IsNullOrEmpty(r.Title)) continue;
                r.Info = ReleaseInfo.Parse(r.Title);
                list.Add(r);
            }
            return list;
        }

        /// RFC 822 dates; the weekday is dropped because some indexers get it wrong.
        static DateTime ParseDate(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return default;
            var comma = s.IndexOf(',');
            if (comma >= 0 && comma <= 4) s = s.Substring(comma + 1);
            return DateTimeOffset.TryParse(s.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d)
                ? d.UtcDateTime : default;
        }

        static long ToLong(string s) => long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

        static string NormalizeImdb(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            s = s.Trim();
            if (s.StartsWith("tt")) return s;
            return long.TryParse(s, out var n) && n > 0 ? "tt" + n.ToString("D7") : null;
        }
    }
}
