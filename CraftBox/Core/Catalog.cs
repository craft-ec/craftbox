using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace CraftBox.Core
{
    /// What a release name says about its content.
    public class ReleaseInfo
    {
        public string Title = "";      // display title: movie name or show name
        public int Year;
        public int Season = -1;        // -1 = not a series
        public int Episode = -1;       // -1 = whole season pack
        public string Quality = "";    // 2160p / 1080p / 720p / 480p
        public string Source = "";     // WEB / BluRay / HDTV / TS / CAM / etc.
        public bool Hevc;
        public bool Dts;               // 2021 Samsung TVs cannot decode DTS audio
        public bool IsSeries => Season >= 0;
        public bool IsLowQuality => Source == "TS" || Source == "TC" || Source == "CAM" || Source == "SCR";

        static readonly Regex Episode1 = new Regex(@"^(?<t>.*?)[\s._\-\[\(]+S(?<s>\d{1,2})[\s._\-]?E(?<e>\d{1,3})", RegexOptions.IgnoreCase);
        static readonly Regex Episode2 = new Regex(@"^(?<t>.*?)[\s._\-\[\(]+(?<s>\d{1,2})x(?<e>\d{2,3})(?!\d)", RegexOptions.IgnoreCase);
        static readonly Regex SeasonPack = new Regex(@"^(?<t>.*?)[\s._\-\[\(]+(?:S(?<s>\d{1,2})(?![\dE])|Season[\s._]?(?<s>\d{1,2}))", RegexOptions.IgnoreCase);
        static readonly Regex MovieYear = new Regex(@"^(?<t>.+)[\s._\-\[\(]+(?<y>(?:19|20)\d{2})(?:[\s._\-\]\)]|$)");
        static readonly Regex QualityRx = new Regex(@"\b(2160p|1080p|720p|576p|480p|4K)\b", RegexOptions.IgnoreCase);
        static readonly Regex HevcRx = new Regex(@"\b(x265|h\.?265|hevc)\b", RegexOptions.IgnoreCase);
        static readonly Regex DtsRx = new Regex(@"\bDTS", RegexOptions.IgnoreCase);
        static readonly Regex SourceRx = new Regex(@"\b(WEB[\s._\-]?DL|WEBRip|WEB|Blu[\s._\-]?Ray|BDRip|BRRip|HDTV|DVDRip|DVD|TELESYNC|HDTS|HD[\s._\-]?TS|(?<!\w)TS(?!\w)|TELECINE|(?<!\w)TC(?!\w)|CAMRIP|(?<!\w)CAM(?!\w)|SCREENER|(?<!\w)SCR(?!\w))\b", RegexOptions.IgnoreCase);
        static readonly Regex YearInShow = new Regex(@"[\s._\(]+(?<y>(?:19|20)\d{2})\)?$");

        public static ReleaseInfo Parse(string name)
        {
            var info = new ReleaseInfo();
            var q = QualityRx.Match(name);
            if (q.Success) info.Quality = q.Value.ToUpperInvariant() == "4K" ? "2160p" : q.Value.ToLowerInvariant();
            info.Hevc = HevcRx.IsMatch(name);
            info.Dts = DtsRx.IsMatch(name);
            var src = SourceRx.Match(name);
            if (src.Success) info.Source = NormalizeSource(src.Value);

            Match m;
            if ((m = Episode1.Match(name)).Success || (m = Episode2.Match(name)).Success)
            {
                info.Season = int.Parse(m.Groups["s"].Value);
                info.Episode = int.Parse(m.Groups["e"].Value);
                SetShowTitle(info, m.Groups["t"].Value);
            }
            else if ((m = SeasonPack.Match(name)).Success && m.Groups["t"].Value.Length > 0)
            {
                info.Season = int.Parse(m.Groups["s"].Value);
                SetShowTitle(info, m.Groups["t"].Value);
            }
            else if ((m = MovieYear.Match(name)).Success && m.Groups["t"].Value.Length > 0)
            {
                info.Title = Clean(m.Groups["t"].Value);
                info.Year = int.Parse(m.Groups["y"].Value);
            }
            else
            {
                info.Title = Clean(name);
            }
            return info;
        }

        static void SetShowTitle(ReleaseInfo info, string raw)
        {
            var t = Clean(raw);
            var y = YearInShow.Match(t);
            if (y.Success)
            {
                info.Year = int.Parse(y.Groups["y"].Value);
                t = t.Substring(0, y.Index).Trim();
            }
            info.Title = t;
        }

        static string NormalizeSource(string s)
        {
            var u = s.ToUpperInvariant().Replace("-", "").Replace(".", "").Replace("_", "").Replace(" ", "");
            if (u == "WEBDL" || u == "WEBRIP" || u == "WEB") return "WEB";
            if (u == "BLURAY" || u == "BDRIP" || u == "BRRIP") return "BluRay";
            if (u == "TS" || u == "TELESYNC" || u == "HDTS") return "TS";
            if (u == "TC" || u == "TELECINE") return "TC";
            if (u == "CAM" || u == "CAMRIP") return "CAM";
            if (u == "SCR" || u == "SCREENER") return "SCR";
            if (u == "DVDRIP" || u == "DVD") return "DVD";
            if (u == "HDTV") return "HDTV";
            return u;
        }

        static string Clean(string s)
        {
            s = Regex.Replace(s, @"^\[[^\]]*\]\s*", "");   // leading [group] tag
            s = s.Replace('.', ' ').Replace('_', ' ');
            s = Regex.Replace(s, @"\s+", " ").Trim(' ', '-', '(', '[');
            return s;
        }

        /// Lowercase, accents and punctuation stripped: groups "Show.Name" with "Show Name!".
        public static string Key(string title)
        {
            var d = title.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder();
            foreach (var ch in d)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
                if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
                else if (sb.Length > 0 && sb[sb.Length - 1] != ' ') sb.Append(' ');
            }
            var key = sb.ToString().Trim();
            return key.StartsWith("the ") ? key.Substring(4) : key;
        }
    }

    public enum TitleKind { Movie, Show }

    public class Title
    {
        public TitleKind Kind;
        public string Name = "";
        public int Year;
        public string Key = "";
        public string ImdbId;
        public int TmdbId;
        public string PosterUrl;
        public string BackdropUrl;
        public string Overview;
        public string Genres;
        public float Rating;
        public string Certification;
        public DateTime Latest;
        public DateTime ReleaseDate;
        public List<Release> Releases = new List<Release>();   // movies, and season packs for shows

        public bool IsUpcoming => ReleaseDate > DateTime.UtcNow;

        public int BestSeeders => Releases.Count == 0 ? 0 : Releases.Max(r => r.Seeders);

        public IEnumerable<int> Seasons =>
            Releases.Where(r => r.Info.Season >= 0).Select(r => r.Info.Season).Distinct().OrderBy(s => s);

        /// Episodes in a season, each with its releases best-first. Season packs are offered under episode -1.
        public SortedDictionary<int, List<Release>> Episodes(int season)
        {
            var map = new SortedDictionary<int, List<Release>>();
            foreach (var r in Releases.Where(r => r.Info.Season == season))
            {
                if (!map.TryGetValue(r.Info.Episode, out var l)) map[r.Info.Episode] = l = new List<Release>();
                l.Add(r);
            }
            foreach (var l in map.Values) Rank(l);
            return map;
        }

        public List<Release> RankedReleases() { var l = Releases.ToList(); Rank(l); return l; }

        /// Best first: playable audio, then healthy swarm, then quality.
        public static void Rank(List<Release> list)
        {
            list.Sort((a, b) =>
            {
                int c = a.Info.IsLowQuality.CompareTo(b.Info.IsLowQuality);
                if (c != 0) return c;
                c = a.Info.Dts.CompareTo(b.Info.Dts);
                if (c != 0) return c;
                c = Health(b).CompareTo(Health(a));
                if (c != 0) return c;
                return QualityRank(b.Info.Quality).CompareTo(QualityRank(a.Info.Quality));
            });
        }

        static int Health(Release r) => r.Seeders >= 20 ? 3 : r.Seeders >= 5 ? 2 : r.Seeders >= 1 ? 1 : 0;

        public static int QualityRank(string q)
        {
            switch (q) { case "2160p": return 4; case "1080p": return 3; case "720p": return 2; case "576p": case "480p": return 1; default: return 0; }
        }
    }

    public static class Catalog
    {
        /// Groups raw releases into titles: one per movie (title+year) and one per show.
        public static List<Title> Build(IEnumerable<Release> releases)
        {
            var map = new Dictionary<string, Title>();
            foreach (var r in releases)
            {
                var info = r.Info ?? (r.Info = ReleaseInfo.Parse(r.Title));
                bool series = info.IsSeries || (r.Categories.Count > 0 && r.Categories.All(Cat.IsTv));
                if (series && !info.IsSeries) continue;   // a TV release we cannot place in a season
                var kind = series ? TitleKind.Show : TitleKind.Movie;
                var key = (series ? "show:" : "movie:") + ReleaseInfo.Key(info.Title) + (series ? "" : ":" + info.Year);
                if (ReleaseInfo.Key(info.Title).Length == 0) continue;

                if (!map.TryGetValue(key, out var t))
                {
                    map[key] = t = new Title { Kind = kind, Name = info.Title, Year = info.Year, Key = key };
                }
                if (t.ImdbId == null) t.ImdbId = r.ImdbId;
                if (t.TmdbId == 0) t.TmdbId = r.TmdbId;
                if (t.Year == 0) t.Year = info.Year;
                if (t.PosterUrl == null && !string.IsNullOrEmpty(r.PosterUrl))
                    t.PosterUrl = r.PosterUrl;
                if (r.Published > t.Latest) t.Latest = r.Published;
                if (!t.Releases.Any(x => x.Link == r.Link)) t.Releases.Add(r);
            }
            return map.Values.OrderByDescending(t => t.Latest).ThenByDescending(t => t.BestSeeders).ToList();
        }
    }
}
