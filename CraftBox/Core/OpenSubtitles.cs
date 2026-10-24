using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CraftBox.Core
{
    public class SubtitleEntry
    {
        public string Id;
        public string Url;
        public string FileName;
        public string ReleaseName;
        public bool HearingImpaired;
        public string Lang;

        public string DisplayName
        {
            get
            {
                var tag = HearingImpaired ? " [SDH]" : "";
                if (!string.IsNullOrEmpty(ReleaseName))
                    return ReleaseName + tag;
                if (!string.IsNullOrEmpty(FileName))
                    return Path.GetFileNameWithoutExtension(FileName) + tag;
                return "Subtitle " + Id + tag;
            }
        }
    }

    public class OpenSubtitlesClient
    {
        const string Base = "https://opensubtitles-v3.strem.io/subtitles";

        public async Task<List<SubtitleEntry>> SearchMovieAsync(string imdbId, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(imdbId)) return new List<SubtitleEntry>();
            var url = $"{Base}/movie/{imdbId}.json";
            return await FetchListAsync(url, ct).ConfigureAwait(false);
        }

        public async Task<List<SubtitleEntry>> SearchEpisodeAsync(string imdbId, int season, int episode, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(imdbId)) return new List<SubtitleEntry>();
            var url = $"{Base}/series/{imdbId}:{season}:{episode}.json";
            return await FetchListAsync(url, ct).ConfigureAwait(false);
        }

        async Task<List<SubtitleEntry>> FetchListAsync(string url, CancellationToken ct)
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
            return ParseList(json);
        }

        static List<SubtitleEntry> ParseList(string json)
        {
            var list = new List<SubtitleEntry>();
            using (var doc = JsonDocument.Parse(json))
            {
                if (!doc.RootElement.TryGetProperty("subtitles", out var subs)) return list;
                foreach (var s in subs.EnumerateArray())
                {
                    if (!s.TryGetProperty("lang", out var lang) || lang.GetString() != "eng") continue;
                    if (!s.TryGetProperty("url", out var u) || u.ValueKind != JsonValueKind.String) continue;

                    var fileName = Str(s, "subtitleFileName");
                    var releaseName = Str(s, "movieReleaseName");
                    var releaseGroup = Str(s, "releaseGroup");

                    bool hi = releaseGroup.Equals("SDH", StringComparison.OrdinalIgnoreCase)
                           || fileName.IndexOf("SDH", StringComparison.OrdinalIgnoreCase) >= 0
                           || fileName.IndexOf("hearing", StringComparison.OrdinalIgnoreCase) >= 0
                           || fileName.IndexOf(".HI.", StringComparison.OrdinalIgnoreCase) >= 0;

                    list.Add(new SubtitleEntry
                    {
                        Id = Str(s, "id"),
                        Url = u.GetString(),
                        FileName = fileName,
                        ReleaseName = releaseName,
                        HearingImpaired = hi,
                        Lang = "eng",
                    });
                }
            }
            // SDH first, then by ID descending (newest first)
            list.Sort((a, b) =>
            {
                if (a.HearingImpaired != b.HearingImpaired)
                    return a.HearingImpaired ? -1 : 1;
                return string.Compare(b.Id, a.Id, StringComparison.Ordinal);
            });
            return list;
        }

        public async Task<string> DownloadAsync(SubtitleEntry entry, AppCache cache, CancellationToken ct = default)
        {
            var id = entry.Id ?? "sub";
            var existing = cache.GetSubtitle(id);
            if (existing != null) return existing;

            byte[] data;
            try
            {
                data = await DohResolver.FetchBytesAsync(entry.Url, ct).ConfigureAwait(false);
            }
            catch
            {
                using (var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(15) })
                    data = await http.GetByteArrayAsync(entry.Url).ConfigureAwait(false);
            }

            return cache.PutSubtitle(id, data);
        }

        static string Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : "";
    }
}
