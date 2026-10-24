using System;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CraftBox.Core
{
    public class SyncClient
    {
        readonly HttpClient http;
        readonly string baseUrl;
        readonly string token;
        public Action<string> Log;

        public bool Enabled => !string.IsNullOrEmpty(baseUrl) && !string.IsNullOrEmpty(token);

        public SyncClient(HttpClient http, string baseUrl = null, string token = null)
        {
            this.http = http;
            this.baseUrl = (baseUrl ?? "").TrimEnd('/');
            this.token = token ?? "";
        }

        public async Task<string> GetAsync(string key, CancellationToken ct = default)
        {
            try
            {
                var req = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/sync/{key}");
                req.Headers.Add("Authorization", $"Bearer {token}");
                var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) return null;
                return await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            }
            catch (Exception e)
            {
                Log?.Invoke($"SYNC GET {key} error: {e.Message}");
                return null;
            }
        }

        public async Task<bool> PutAsync(string key, string json, CancellationToken ct = default)
        {
            try
            {
                var req = new HttpRequestMessage(HttpMethod.Put, $"{baseUrl}/sync/{key}");
                req.Headers.Add("Authorization", $"Bearer {token}");
                req.Content = new StringContent(json, Encoding.UTF8, "application/json");
                var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
                return resp.IsSuccessStatusCode;
            }
            catch (Exception e)
            {
                Log?.Invoke($"SYNC PUT {key} error: {e.Message}");
                return false;
            }
        }

        public async Task SyncAccountAsync(AppCache cache, string accountId, CancellationToken ct = default)
        {
            if (!Enabled) return;
            var sw = System.Diagnostics.Stopwatch.StartNew();

            var historyKey = $"history-{accountId}";
            var pinnedKey = $"pinned-{accountId}";
            var likesKey = $"likes-{accountId}";
            var dislikesKey = $"dislikes-{accountId}";

            var localHistory = cache.GetText($"play_history_{accountId}");
            var localPinned = cache.GetText($"pinned_titles_{accountId}");
            var localLikes = cache.GetText($"likes_{accountId}");
            var localDislikes = cache.GetText($"dislikes_{accountId}");

            int pushed = 0, pulled = 0;

            if (!string.IsNullOrEmpty(localHistory))
            { if (await PutAsync(historyKey, localHistory, ct)) pushed++; }
            else
            {
                var remote = await GetAsync(historyKey, ct);
                if (!string.IsNullOrEmpty(remote) && remote != "null")
                { cache.PutText($"play_history_{accountId}", remote); pulled++; }
            }

            if (!string.IsNullOrEmpty(localPinned))
            { if (await PutAsync(pinnedKey, localPinned, ct)) pushed++; }
            else
            {
                var remote = await GetAsync(pinnedKey, ct);
                if (!string.IsNullOrEmpty(remote) && remote != "null")
                { cache.PutText($"pinned_titles_{accountId}", remote); pulled++; }
            }

            if (!string.IsNullOrEmpty(localLikes))
            { if (await PutAsync(likesKey, localLikes, ct)) pushed++; }
            else
            {
                var remote = await GetAsync(likesKey, ct);
                if (!string.IsNullOrEmpty(remote) && remote != "null")
                { cache.PutText($"likes_{accountId}", remote); pulled++; }
            }

            if (!string.IsNullOrEmpty(localDislikes))
            { if (await PutAsync(dislikesKey, localDislikes, ct)) pushed++; }
            else
            {
                var remote = await GetAsync(dislikesKey, ct);
                if (!string.IsNullOrEmpty(remote) && remote != "null")
                { cache.PutText($"dislikes_{accountId}", remote); pulled++; }
            }

            Log?.Invoke($"SYNC account={accountId} pushed={pushed} pulled={pulled} time={sw.ElapsedMilliseconds}ms");
        }
    }
}
