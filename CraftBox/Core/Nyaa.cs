using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;

namespace CraftBox.Core
{
    public class NyaaClient
    {
        const string Base = "https://nyaa.si";

        static readonly string[] Trackers =
        {
            "udp://tracker.opentrackr.org:1337/announce",
            "udp://open.stealth.si:80/announce",
            "udp://tracker.torrent.eu.org:451/announce",
            "udp://tracker.openbittorrent.com:6969/announce",
        };

        static readonly Regex SizeRx = new Regex(@"([\d.]+)\s*(TiB|GiB|MiB|KiB|TB|GB|MB|KB)", RegexOptions.IgnoreCase);

        public async Task<List<Release>> SearchAsync(string query, int limit = 50, CancellationToken ct = default)
        {
            var url = $"{Base}/?page=rss&q={Uri.EscapeDataString(query)}&c=1_0&f=0";
            string xml;
            try
            {
                xml = await DohResolver.FetchAsync(url, ct).ConfigureAwait(false);
            }
            catch
            {
                using (var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(15) })
                    xml = await http.GetStringAsync(url).ConfigureAwait(false);
            }
            return Parse(xml, limit);
        }

        static List<Release> Parse(string xml, int limit)
        {
            var list = new List<Release>();
            var doc = new XmlDocument();
            doc.LoadXml(xml);
            var ns = new XmlNamespaceManager(doc.NameTable);
            ns.AddNamespace("nyaa", "https://nyaa.si/xmlns/nyaa");

            var items = doc.SelectNodes("//item");
            if (items == null) return list;

            foreach (XmlNode item in items)
            {
                if (list.Count >= limit) break;
                var title = item.SelectSingleNode("title")?.InnerText?.Trim();
                var hash = item.SelectSingleNode("nyaa:infoHash", ns)?.InnerText?.Trim();
                if (string.IsNullOrEmpty(title) || string.IsNullOrEmpty(hash)) continue;

                int seeders = 0;
                var sn = item.SelectSingleNode("nyaa:seeders", ns)?.InnerText;
                if (sn != null) int.TryParse(sn, out seeders);

                int leechers = 0;
                var ln = item.SelectSingleNode("nyaa:leechers", ns)?.InnerText;
                if (ln != null) int.TryParse(ln, out leechers);

                long size = 0;
                var sizeStr = item.SelectSingleNode("nyaa:size", ns)?.InnerText;
                if (sizeStr != null) size = ParseSize(sizeStr);

                var magnet = BuildMagnet(hash, title);
                var r = new Release
                {
                    Title = title,
                    Indexer = "Nyaa",
                    Link = magnet,
                    Size = size,
                    Seeders = seeders,
                    Peers = leechers,
                };
                var pubDate = item.SelectSingleNode("pubDate")?.InnerText;
                if (!string.IsNullOrEmpty(pubDate) && DateTime.TryParse(pubDate, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal, out var dt))
                    r.Published = dt;
                r.Info = ReleaseInfo.Parse(title);
                r.Categories.Add(Cat.Tv);
                list.Add(r);
            }
            return list;
        }

        static long ParseSize(string s)
        {
            var m = SizeRx.Match(s);
            if (!m.Success) return 0;
            if (!double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return 0;
            var u = m.Groups[2].Value.ToUpperInvariant();
            if (u == "TIB" || u == "TB") return (long)(v * 1024L * 1024 * 1024 * 1024);
            if (u == "GIB" || u == "GB") return (long)(v * 1024L * 1024 * 1024);
            if (u == "MIB" || u == "MB") return (long)(v * 1024L * 1024);
            return (long)(v * 1024);
        }

        static string BuildMagnet(string hash, string name)
        {
            var magnet = $"magnet:?xt=urn:btih:{hash}&dn={Uri.EscapeDataString(name)}";
            foreach (var tr in Trackers)
                magnet += "&tr=" + Uri.EscapeDataString(tr);
            return magnet;
        }
    }
}
