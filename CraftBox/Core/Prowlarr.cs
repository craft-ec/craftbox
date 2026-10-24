using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CraftBox.Core
{
    public static class Prowlarr
    {
        public static async Task<List<IndexerConfig>> DiscoverAsync(
            string baseUrl, string apiKey, CancellationToken ct = default)
        {
            baseUrl = baseUrl.TrimEnd('/');
            if (!baseUrl.StartsWith("http://") && !baseUrl.StartsWith("https://"))
                baseUrl = "http://" + baseUrl;
            var url = baseUrl + "/api/v1/indexer";
            using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) })
            using (var req = new HttpRequestMessage(HttpMethod.Get, url))
            {
                if (!string.IsNullOrEmpty(apiKey))
                    req.Headers.TryAddWithoutValidation("X-Api-Key", apiKey);
                HttpResponseMessage res;
                try
                {
                    res = await http.SendAsync(req, ct).ConfigureAwait(false);
                }
                catch (TaskCanceledException)
                {
                    throw new Exception($"Timed out connecting to {baseUrl} — make sure the TV can reach this address");
                }
                catch (HttpRequestException e)
                {
                    throw new Exception($"Cannot reach {baseUrl}: {e.Message}");
                }
                using (res)
                {
                    var body = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!res.IsSuccessStatusCode)
                        throw new Exception($"Prowlarr returned {(int)res.StatusCode}: {body.Substring(0, Math.Min(body.Length, 200))}");
                    return ParseIndexers(body, baseUrl, apiKey);
                }
            }
        }

        static List<IndexerConfig> ParseIndexers(string json, string baseUrl, string apiKey)
        {
            var list = new List<IndexerConfig>();
            using (var doc = JsonDocument.Parse(json))
            {
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    if (el.TryGetProperty("enable", out var en) && !en.GetBoolean())
                        continue;
                    var id = el.GetProperty("id").GetInt32();
                    var name = el.TryGetProperty("name", out var n) ? n.GetString() : $"Indexer {id}";
                    var feedUrl = baseUrl + "/" + id + "/api";
                    list.Add(new IndexerConfig { Name = name, Url = feedUrl, ApiKey = apiKey });
                }
            }
            return list;
        }
    }
}
