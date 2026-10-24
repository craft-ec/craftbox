using System;
using System.Collections.Generic;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;

namespace CraftBox.UI
{
    static class Theme
    {
        // Stream-repo Netflix-style palette
        public static readonly Color Bg = new Color(0.078f, 0.078f, 0.078f, 1f);        // #141414
        public static readonly Color Surface = new Color(0.043f, 0.043f, 0.043f, 1f);   // #0b0b0b
        public static readonly Color Panel = new Color(0.141f, 0.141f, 0.141f, 1f);      // #242424
        public static readonly Color PanelHover = new Color(0.2f, 0.2f, 0.2f, 1f);       // #333333
        public static readonly Color FocusBg = new Color(0.898f, 0.035f, 0.078f, 1f);    // #e50914 accent
        public static readonly Color Accent = new Color(0.898f, 0.035f, 0.078f, 1f);     // #e50914
        public static readonly Color Text = new Color(0.96f, 0.96f, 0.96f, 1f);          // #f5f5f5
        public static readonly Color Muted = new Color(0.667f, 0.667f, 0.667f, 1f);      // #aaaaaa
        public static readonly Color Warn = new Color(1f, 0.72f, 0.36f, 1f);
        public static readonly Color White = new Color(1f, 1f, 1f, 1f);
        public static readonly Color CardBg = new Color(0.149f, 0.149f, 0.149f, 1f);     // #262626
        public static readonly Color Overlay = new Color(0, 0, 0, 0.78f);
        public const int W = 1920, H = 1080, Margin = 80;

        // Card dimensions: 2:3 aspect ratio posters
        public const float CardW = 185, CardH = 278, CardGap = 18, CardRadius = 6;
        // Row layout
        public const float RowLabelH = 44, RowPadTop = 12, RowPadBot = 20;
        public static float RowH => RowLabelH + RowPadTop + CardH + 50 + RowPadBot;
        // Featured hero
        public const float HeroH = 520;

        public static TextLabel Label(string text, float px, Color color, float x, float y, float w, float h = 0, bool multiline = false)
        {
            var l = new TextLabel
            {
                Text = text ?? "",
                PixelSize = px,
                TextColor = color,
                Position = new Position(x, y),
                MultiLine = multiline,
                Ellipsis = true,
                VerticalAlignment = VerticalAlignment.Center,
            };
            l.Size = new Size(w, h > 0 ? h : px * 1.5f);
            return l;
        }

        public static View Box(float x, float y, float w, float h, Color color) =>
            new View { Position = new Position(x, y), Size = new Size(w, h), BackgroundColor = color };

        public static View RoundBox(float x, float y, float w, float h, Color color, float radius = 0)
        {
            var v = new View { Position = new Position(x, y), Size = new Size(w, h), BackgroundColor = color };
            return v;
        }

        public static View MakeCard(Core.Title t, float cw, float ch, float radius,
            int progressPct = -1, string certLabel = null, float labelH = 44)
        {
            var card = new View { Size = new Size(cw, ch + labelH) };
            var ring = new View
            {
                Name = "ring",
                Size = new Size(cw + 6, ch + 6),
                Position = new Position(-3, -3),
                BackgroundColor = Accent,
            };
            ring.Hide();
            card.Add(ring);
            var frame = RoundBox(0, 0, cw, ch, CardBg, radius);
            frame.Name = "frame";
            frame.ClippingMode = ClippingModeType.ClipChildren;
            var initials = Label(t.Name, Math.Min(22, cw * 0.12f), Muted, 10, 0, cw - 20, ch, true);
            initials.HorizontalAlignment = HorizontalAlignment.Center;
            initials.Name = "initials";
            frame.Add(initials);
            card.Add(frame);
            if (progressPct >= 0)
            {
                card.Add(Box(0, ch - 4, cw, 4, new Color(0.3f, 0.3f, 0.3f, 1f)));
                card.Add(Box(0, ch - 4, cw * progressPct / 100f, 4, Accent));
            }
            if (!string.IsNullOrEmpty(certLabel))
            {
                int bw = certLabel.Length > 3 ? 50 : 34;
                var badge = Label(certLabel, 12, new Color(1f, 1f, 1f, 0.95f),
                    cw - bw - 4, 4, bw, 18);
                badge.HorizontalAlignment = HorizontalAlignment.Center;
                badge.BackgroundColor = new Color(0f, 0f, 0f, 0.7f);
                card.Add(badge);
            }
            float titlePx = cw >= 200 ? 24 : 20;
            float subPx = cw >= 200 ? 20 : 16;
            var titleLbl = Label(t.Name, titlePx, Text, 0, ch + 6, cw, titlePx + 8);
            titleLbl.Name = "title";
            card.Add(titleLbl);
            var sub = (t.Year > 0 ? t.Year + " · " : "") + (t.Kind == Core.TitleKind.Show ? "Series" : "Movie");
            if (t.Rating > 0) sub += $" · ★{t.Rating:0.0}";
            card.Add(Label(sub, subPx, Muted, 0, ch + titlePx + 12, cw, subPx + 4));
            return card;
        }

        public static void SetCardPoster(View card, string url, float cw, float ch)
        {
            var frame = card.FindChildByName("frame");
            if (frame == null || frame.FindChildByName("poster") != null) return;
            var img = new ImageView
            {
                Name = "poster", ResourceUrl = url,
                Size = new Size(cw, ch), FittingMode = FittingModeType.ScaleToFill
            };
            frame.Add(img);
            var init = frame.FindChildByName("initials");
            if (init != null) init.Hide();
        }

        public static string Bytes(long b) =>
            b >= 1L << 30 ? (b / (double)(1L << 30)).ToString("0.0") + " GB" : (b / (double)(1L << 20)).ToString("0") + " MB";

        public static string Time(long ms)
        {
            var t = TimeSpan.FromMilliseconds(Math.Max(0, ms));
            return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
        }
    }

    abstract class Screen : View
    {
        protected readonly App App;
        protected Screen(App app)
        {
            App = app;
            Size = new Size(Theme.W, Theme.H);
            BackgroundColor = Theme.Bg;
        }
        public abstract bool OnKey(string key);
        public virtual void OnShown() { }
        public virtual void OnClosed() { }
    }

    class FocusList : View
    {
        readonly float rowH;
        readonly int visible;
        readonly List<View> rows = new List<View>();
        readonly View content = new View();
        public int Index { get; private set; }
        public int Count => rows.Count;
        public bool Active = true;
        public event Action<int> Activated;
        public event Action<int> Moved;
        float imgW, imgH;

        public FocusList(float x, float y, float w, float h, float rowHeight = 72)
        {
            Position = new Position(x, y);
            Size = new Size(w, h);
            ClippingMode = ClippingModeType.ClipChildren;
            rowH = rowHeight;
            visible = Math.Max(1, (int)(h / rowHeight));
            Add(content);
        }

        public void SetItems(IEnumerable<(string main, string sub, Color subColor)> items)
        {
            imgW = imgH = 0;
            foreach (var r in rows) { content.Remove(r); r.Dispose(); }
            rows.Clear();
            float w = Size.Width;
            foreach (var (main, sub, subColor) in items)
            {
                var row = Theme.RoundBox(0, rows.Count * rowH, w, rowH - 6, Theme.Panel, 4);
                row.Add(Theme.Label(main, 26, Theme.Text, 20, 4, w - 40, 36));
                if (!string.IsNullOrEmpty(sub)) row.Add(Theme.Label(sub, 20, subColor ?? Theme.Muted, 20, 38, w - 40, 26));
                content.Add(row);
                rows.Add(row);
            }
            Index = 0;
            content.PositionY = 0;
            Refresh();
        }

        public void SetImageItems(IEnumerable<(string main, string sub, Color subColor)> items, float imageW, float imageH)
        {
            imgW = imageW;
            imgH = imageH;
            foreach (var r in rows) { content.Remove(r); r.Dispose(); }
            rows.Clear();
            float w = Size.Width;
            float textX = imageW + 16;
            float textW = w - textX - 12;
            foreach (var (main, sub, subColor) in items)
            {
                var row = Theme.RoundBox(0, rows.Count * rowH, w, rowH - 6, Theme.Panel, 4);
                var iv = new ImageView
                {
                    Position = new Position(6, (rowH - 6 - imageH) / 2),
                    Size = new Size(imageW, imageH),
                    BackgroundColor = Theme.Surface,
                    FittingMode = FittingModeType.ScaleToFill,
                };
                row.Add(iv);
                row.Add(Theme.Label(main, 24, Theme.Text, textX, 8, textW, 30));
                if (!string.IsNullOrEmpty(sub)) row.Add(Theme.Label(sub, 18, subColor ?? Theme.Muted, textX, 40, textW, 24));
                content.Add(row);
                rows.Add(row);
            }
            Index = 0;
            content.PositionY = 0;
            Refresh();
        }

        public void SetRowImage(int index, string localPath)
        {
            if (index < 0 || index >= rows.Count || string.IsNullOrEmpty(localPath)) return;
            var row = rows[index];
            for (uint i = 0; i < row.ChildCount; i++)
            {
                if (row.GetChildAt(i) is ImageView iv)
                {
                    iv.ResourceUrl = localPath;
                    return;
                }
            }
        }

        public void SetActive(bool active) { Active = active; Refresh(); }

        void Refresh()
        {
            for (int i = 0; i < rows.Count; i++)
                rows[i].BackgroundColor = i == Index && Active ? Theme.PanelHover : Theme.Panel;
            int top = (int)(-content.PositionY / rowH);
            if (Index < top) top = Index;
            if (Index >= top + visible) top = Index - visible + 1;
            content.PositionY = -top * rowH;
        }

        public bool OnKey(string key)
        {
            if (rows.Count == 0) return false;
            switch (key)
            {
                case "Up": if (Index == 0) return false; Index--; break;
                case "Down": if (Index == rows.Count - 1) return false; Index++; break;
                case "Return": case "KP_Enter": Activated?.Invoke(Index); return true;
                default: return false;
            }
            Refresh();
            Moved?.Invoke(Index);
            return true;
        }
    }
}
