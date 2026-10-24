using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CraftBox.Core
{
    public class PirateBayClient
    {
        readonly HttpClient http;

        static readonly string[] Trackers =
        {
            "udp://tracker.opentrackr.org:1337/announce",
            "udp://open.stealth.si:80/announce",
            "udp://tracker.torrent.eu.org:451/announce",
            "udp://tracker.dler.org:6969/announce",
            "udp://explodie.org:6969/announce",
            "udp://tracker.openbittorrent.com:6969/announce",
        };

        public PirateBayClient(HttpClient http) { this.http = http; }

        public async Task<List<Release>> SearchAsync(string baseUrl, string query, int limit = 30, CancellationToken ct = default)
        {
            var url = baseUrl.TrimEnd('/') + $"?q={Uri.EscapeDataString(query)}&cat=0";
            return await FetchAsync(url, limit, ct).ConfigureAwait(false);
        }

        static readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
        static DateTime lastRequest = DateTime.MinValue;
        static DateTime rateLimitedUntil = DateTime.MinValue;

        async Task<List<Release>> FetchAsync(string url, int limit, CancellationToken ct)
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // Respect rate limit cooldown
                var now = DateTime.UtcNow;
                if (rateLimitedUntil > now)
                {
                    var wait = rateLimitedUntil - now;
                    StreamEngine.Log?.Invoke($"TPB rate-limit cooldown {wait.TotalSeconds:0}s");
                    await Task.Delay(wait, ct).ConfigureAwait(false);
                }

                // Minimum 3s gap between requests
                var elapsed = DateTime.UtcNow - lastRequest;
                if (elapsed.TotalSeconds < 3)
                    await Task.Delay(TimeSpan.FromSeconds(3) - elapsed, ct).ConfigureAwait(false);

                for (int attempt = 0; attempt < 3; attempt++)
                {
                    lastRequest = DateTime.UtcNow;
                    try
                    {
                        using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                        {
                            cts.CancelAfter(TimeSpan.FromSeconds(15));
                            using (var res = await http.GetAsync(url, cts.Token).ConfigureAwait(false))
                            {
                                if (res.StatusCode == (HttpStatusCode)429)
                                {
                                    var delay = (attempt + 1) * 10;
                                    StreamEngine.Log?.Invoke($"TPB 429, retry {attempt + 1}/3 after {delay}s");
                                    rateLimitedUntil = DateTime.UtcNow.AddSeconds(delay + 5);
                                    await Task.Delay(delay * 1000, ct).ConfigureAwait(false);
                                    continue;
                                }
                                var body = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
                                if (!res.IsSuccessStatusCode)
                                    throw new Exception($"TPB {(int)res.StatusCode}");
                                return Parse(body, limit);
                            }
                        }
                    }
                    catch (Exception) when (!ct.IsCancellationRequested)
                    {
                        if (attempt < 2)
                            await Task.Delay((attempt + 1) * 3000, ct).ConfigureAwait(false);
                    }
                }
                return await FetchWithDohAsync(url, limit, ct).ConfigureAwait(false);
            }
            finally { gate.Release(); }
        }

        async Task<List<Release>> FetchWithDohAsync(string url, int limit, CancellationToken ct)
        {
            lastRequest = DateTime.UtcNow;
            try
            {
                var body = await DohResolver.FetchAsync(url, ct).ConfigureAwait(false);
                return Parse(body, limit);
            }
            catch (Exception e) when (!ct.IsCancellationRequested && e.Message.Contains("429"))
            {
                rateLimitedUntil = DateTime.UtcNow.AddSeconds(30);
                StreamEngine.Log?.Invoke($"TPB DoH 429, cooldown 30s");
                throw;
            }
        }

        static List<Release> Parse(string json, int limit)
        {
            var list = new List<Release>();
            using (var doc = JsonDocument.Parse(json))
            {
                foreach (var t in doc.RootElement.EnumerateArray())
                {
                    if (list.Count >= limit) break;
                    var name = Str(t, "name");
                    var hash = Str(t, "info_hash");
                    if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(hash))
                        continue;
                    if (hash.Replace("0", "").Length == 0) continue;
                    var magnet = BuildMagnet(hash, name);
                    var r = new Release
                    {
                        Title = name,
                        Indexer = "TPB",
                        Link = magnet,
                        Size = Long(t, "size"),
                        Seeders = (int)Long(t, "seeders"),
                        Peers = (int)Long(t, "leechers"),
                    };
                    var epoch = Long(t, "added");
                    if (epoch > 0)
                        r.Published = DateTimeOffset.FromUnixTimeSeconds(epoch).UtcDateTime;
                    r.Info = ReleaseInfo.Parse(name);
                    list.Add(r);
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
