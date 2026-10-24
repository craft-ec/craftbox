using System.Linq;
using Tizen.NUI;
using CraftBox.Core;

namespace CraftBox.UI
{
    /// Settings are edited from a phone (typing URLs with a remote is painful); this screen shows where.
    class SettingsScreen : Screen
    {
        public SettingsScreen(App app) : base(app)
        {
            Add(Theme.Label("Settings", 52, Theme.Accent, Theme.Margin, 40, 800, 72));
            var urls = app.Config.Urls().ToList();
            Add(Theme.Label("On a phone or computer on the same Wi-Fi, open:", 30, Theme.Text, Theme.Margin, 150, 1700, 44));
            Add(Theme.Label(urls.Count > 0 ? string.Join("\n", urls) : "http://<this TV's IP>:8099/", 44, Theme.Accent,
                Theme.Margin, 205, 1700, 70 * System.Math.Max(1, urls.Count), true));

            float y = 230 + 70 * System.Math.Max(1, urls.Count);
            Add(Theme.Label("Indexers", 34, Theme.Text, Theme.Margin, y, 1700, 50));
            y += 60;
            if (app.Settings.Indexers.Count == 0)
            {
                Add(Theme.Label("None yet", 28, Theme.Muted, Theme.Margin, y, 1700, 40));
                y += 44;
            }
            foreach (var ix in app.Settings.Indexers)
            {
                Add(Theme.Label($"{ix.Name} — {ix.Url}", 28, Theme.Muted, Theme.Margin, y, 1700, 40));
                y += 44;
            }
            y += 30;
            Add(Theme.Label("Posters (TMDB): " + (app.Tmdb.Enabled ? "on" : "off"), 28, Theme.Muted, Theme.Margin, y, 1700, 40));
            y += 44;
            var free = app.Engine == null ? "…" : Theme.Bytes(app.Engine.FreeBytes);
            Add(Theme.Label($"Temporary buffer limit: {app.Settings.BufferLimitMb} MB  ·  free on TV: {free}", 28, Theme.Muted, Theme.Margin, y, 1700, 40));
            y += 44;
            Add(Theme.Label("Nothing is kept: pieces are deleted when playback stops.", 28, Theme.Muted, Theme.Margin, y, 1700, 40));
            Add(Theme.Label("Back to return", 26, Theme.Muted, Theme.Margin, Theme.H - 90, 800, 40));
        }

        public override bool OnKey(string key) => false;
    }
}
