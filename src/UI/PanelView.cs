using System;
using System.Collections.Generic;
using Microsoft.UI.Text;
using Windows.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinMonitor.Core;
using Windows.Foundation;
using Color = Windows.UI.Color;

namespace WinMonitor.UI
{
    /// <summary>A group of rows on the panel (CPU, GPU, ...).</summary>
    public sealed class GroupLayoutInfo
    {
        public string GroupName { get; }
        public string Label { get; set; } = "";
        public List<MetricItem> Items { get; }
        public GroupLayoutInfo(string name, List<MetricItem> items) { GroupName = name; Items = items; }
    }

    /// <summary>
    /// Draws the panel with WinUI elements. Layout maths ported from LiteMonitor's UILayout / UIRenderer
    /// (sizes in device-independent pixels; the theme's font sizes are points).
    /// </summary>
    internal sealed class PanelView
    {

        public Grid Root { get; } = new();
        private readonly Border _background = new();
        private readonly Canvas _canvas = new();
        private readonly Dictionary<MetricItem, ItemVisual> _visuals = new();
        private readonly Dictionary<uint, SolidColorBrush> _brushes = new();

        private sealed class ItemVisual
        {
            public TextBlock Label = null!;
            public TextBlock Value = null!;
            public Border? BarFill;
            public double BarWidth;
            public MetricRenderStyle Style;
        }

        public PanelView()
        {
            Root.Children.Add(_background);
            Root.Children.Add(_canvas);
            Root.HorizontalAlignment = HorizontalAlignment.Left;
            Root.VerticalAlignment = VerticalAlignment.Top;
        }

        private static Theme T => ThemeManager.Current;

        // ------------------------------------------------------------------ fonts & brushes
        private static double Pt(double pt) => Math.Max(1, pt * 96.0 / 72.0 * T.UserScale);
        private static int LineH(double pt) => (int)Math.Ceiling(Pt(pt) * 1.33);
        // GDI-style names that carry a weight ("Segoe UI Semibold") are split into family + weight for WinUI
        private static readonly (string Suffix, FontWeight Weight)[] WeightSuffixes =
        {
            (" Semibold", FontWeights.SemiBold), (" SemiBold", FontWeights.SemiBold), (" Semilight", FontWeights.SemiLight),
            (" Bold", FontWeights.Bold), (" Light", FontWeights.Light), (" Black", FontWeights.Black), (" Medium", FontWeights.Medium),
        };
        private readonly Dictionary<string, FontFamily> _families = new();

        private (FontFamily Family, FontWeight Weight) Font(bool value)
        {
            var f = (value && !string.IsNullOrWhiteSpace(T.Font.ValueFamily) ? T.Font.ValueFamily : T.Font.Family)?.Trim() ?? "";
            var w = T.Font.Bold ? FontWeights.Bold : FontWeights.Normal;
            foreach (var (suffix, weight) in WeightSuffixes)
            {
                if (!f.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
                f = f[..^suffix.Length];
                if (!T.Font.Bold || weight.Weight > w.Weight) w = weight;
                break;
            }
            string key = string.IsNullOrEmpty(f) ? "Segoe UI" : f + ", Segoe UI, Segoe UI Emoji";
            if (!_families.TryGetValue(key, out var fam)) _families[key] = fam = new FontFamily(key);
            return (fam, w);
        }

        private SolidColorBrush Brush(Color c)
        {
            uint k = ((uint)c.A << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;
            if (!_brushes.TryGetValue(k, out var b)) _brushes[k] = b = new SolidColorBrush(c);
            return b;
        }
        private SolidColorBrush Brush(string hex) => Brush(ThemeManager.ParseColor(hex));

        private TextBlock Text(string text, double pt, bool valueFont, Color color)
        {
            return new TextBlock
            {
                Text = text,
                FontFamily = Font(valueFont).Family,
                FontSize = Pt(pt),
                FontWeight = Font(valueFont).Weight,
                Foreground = Brush(color),
                TextWrapping = TextWrapping.NoWrap,
                IsColorFontEnabled = !Settings.Load().MonochromeEmoji,
                TextTrimming = TextTrimming.None,
                IsHitTestVisible = false,
            };
        }

        /// <summary>Places a text inside a rectangle with the given alignment (like TextRenderer's flags).</summary>
        private void Place(TextBlock tb, Rect r, HorizontalAlignment h, VerticalAlignment v)
        {
            var cell = new Grid { Width = Math.Max(0, r.Width), Height = Math.Max(0, r.Height), IsHitTestVisible = false };
            tb.HorizontalAlignment = h;
            tb.VerticalAlignment = v;
            cell.Children.Add(tb);
            Canvas.SetLeft(cell, r.X);
            Canvas.SetTop(cell, r.Y);
            _canvas.Children.Add(cell);
        }

        private Border Box(Rect r, double radius, SolidColorBrush fill)
        {
            var b = new Border
            {
                Width = Math.Max(0, r.Width),
                Height = Math.Max(0, r.Height),
                CornerRadius = new CornerRadius(Math.Max(0, Math.Min(radius, Math.Min(r.Width, r.Height) / 2))),
                Background = fill,
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(b, r.X);
            Canvas.SetTop(b, r.Y);
            _canvas.Children.Add(b);
            return b;
        }

        private void Reset(double w, double h)
        {
            _canvas.Children.Clear();
            _visuals.Clear();
            _background.Width = w;
            _background.Height = h;
            _background.CornerRadius = new CornerRadius(Math.Max(0, T.Layout.CornerRadius));
            _background.Background = Brush(T.Color.Background);
            Root.Width = _canvas.Width = w;
            Root.Height = _canvas.Height = h;
        }

        // ------------------------------------------------------------------ vertical panel
        /// <summary>Lays out and draws the panel. Returns its size in DIPs.</summary>
        public Size BuildVertical(List<GroupLayoutInfo> groups, bool showTitle, string title)
        {
            var t = T;
            var L = t.Layout;
            float s = L.LayoutScale <= 0 ? 1f : L.LayoutScale;

            int innerPad = (int)(10 * s), innerPadTotal = innerPad * 2;
            int barMinH = (int)(4 * s);
            int x = L.Padding - 1;   // LiteMonitor's visual compensation
            int y = L.Padding;
            int w = L.Width - L.Padding * 2;
            int rowH = L.RowHeight;
            int textH = (int)(rowH * 0.60);                         // a row's text line
            int barH = Math.Max(barMinH, (int)(rowH * 0.20));       // its bar, right under the text
            int barGap = Math.Max(1, (int)(2 * s));
            int rowGap = rowH - (textH + barGap + barH) + L.ItemGap; // same pitch as before for rows with a bar
            int groupLineH = LineH(t.Font.Group);
            bool hasTitle = showTitle && !string.IsNullOrEmpty(title);

            if (hasTitle) y += rowH + L.Padding;
            else y += groupLineH + L.GroupTitleOffset + (int)(4 * s);   // room for the first group's title

            // pass 1: layout
            var groupRects = new List<Rect>();
            var itemRects = new Dictionary<MetricItem, (Rect label, Rect value, Rect bar, Rect bounds)>();
            for (int idx = 0; idx < groups.Count; idx++)
            {
                var g = groups[idx];
                bool twoCol = g.GroupName is "NET" or "DISK" or "DATA";
                int gp = L.GroupPadding;
                int itemY = y + gp;
                if (twoCol) itemY += L.ItemGap / 2;
                int startY = itemY;
                var buffer = new List<MetricItem>();
                int lastGap = 0;

                void Flush()
                {
                    if (buffer.Count == 0) return;
                    int colW = w / 2;
                    for (int i = 0; i < buffer.Count; i++)
                    {
                        var it = buffer[i];
                        it.Style = MetricRenderStyle.TwoColumn;
                        int ix = i == 0 ? x : x + colW;
                        int half = rowH / 2;
                        itemRects[it] = (new Rect(ix, itemY, colW, half), new Rect(ix, itemY + half, colW, rowH - half), Rect.Empty, new Rect(ix, itemY, colW, rowH));
                    }
                    itemY += rowH + L.ItemGap;
                    buffer.Clear();
                }

                foreach (var it in g.Items)
                {
                    if (twoCol)
                    {
                        buffer.Add(it);
                        if (buffer.Count >= 2) Flush();
                    }
                    else
                    {
                        // text line, then the bar hugging it; every row is followed by the same gap,
                        // so rows with and without a bar space out evenly
                        Flush();
                        it.Style = MetricRenderStyle.StandardBar;
                        bool withBar = it.BoundConfig == null || (it.BoundConfig.ShowBar && it.BoundConfig.HasBar);
                        int h = withBar ? textH + barGap + barH : textH;
                        var top = new Rect(x + innerPad, itemY, w - innerPadTotal, textH);
                        var bar = withBar ? new Rect(top.X, itemY + textH + barGap, top.Width, barH) : Rect.Empty;
                        itemRects[it] = (top, top, bar, new Rect(x, itemY, w, h));
                        itemY += h + rowGap;
                        lastGap = rowGap;
                    }
                }
                Flush();

                int content = itemY - startY;
                if (!twoCol && content > 0) content -= lastGap;
                int groupH = gp * 2 + content;
                groupRects.Add(new Rect(x, y, w, groupH));
                y += idx < groups.Count - 1 ? groupH + L.GroupSpacing + L.GroupBottom : groupH;
            }
            int height = y + L.Padding;

            // pass 2: draw
            Reset(L.Width, height);
            if (hasTitle)
            {
                var tr = new Rect(L.Padding, L.Padding, L.Width - L.Padding * 2, LineH(t.Font.Title) + (int)(4 * s));
                Place(Text(title, t.Font.Title, false, ThemeManager.ParseColor(t.Color.TextTitle)), tr, HorizontalAlignment.Left, VerticalAlignment.Center);
            }

            var groupBg = Brush(t.Color.GroupBackground);
            var primary = ThemeManager.ParseColor(t.Color.TextPrimary);
            var barBg = Brush(t.Color.BarBackground);
            bool narrow = L.Width < 240 * s;
            for (int gi = 0; gi < groups.Count; gi++)
            {
                var g = groups[gi];
                var gr = groupRects[gi];
                Box(gr, L.GroupRadius, groupBg);

                int gp = L.GroupPadding;
                int titleY = (int)gr.Y - L.GroupTitleOffset - groupLineH;
                int extraH = (int)(8 * s);
                var rt = new Rect(gr.X + gp, Math.Max(0, titleY - extraH / 2), Math.Max(0, gr.Width - gp * 2), groupLineH + extraH);
                Place(Text(string.IsNullOrEmpty(g.Label) ? g.GroupName : g.Label, t.Font.Group, false, ThemeManager.ParseColor(t.Color.TextGroup)),
                      rt, HorizontalAlignment.Left, VerticalAlignment.Center);

                for (int i = 0; i < g.Items.Count; i++)
                {
                    var it = g.Items[i];
                    if (!itemRects.TryGetValue(it, out var r)) continue;
                    var v = new ItemVisual { Style = it.Style };
                    string label = string.IsNullOrEmpty(it.Label) ? it.Key : it.Label;
                    var stateColor = it.GetTextColor(t);

                    switch (it.Style)
                    {
                        case MetricRenderStyle.TwoColumn:
                            v.Label = Text(label, t.Font.Item, false, primary);
                            v.Value = Text(it.GetFormattedText(narrow), t.Font.Value, true, stateColor);
                            Place(v.Label, r.label, HorizontalAlignment.Center, VerticalAlignment.Top);
                            Place(v.Value, r.value, HorizontalAlignment.Center, VerticalAlignment.Bottom);
                            break;

                        case MetricRenderStyle.TextOnly:
                            v.Label = Text(label, t.Font.Item, false, primary);
                            v.Value = Text(it.GetFormattedText(false), t.Font.Item, false, stateColor);
                            Place(v.Label, r.label, HorizontalAlignment.Left, VerticalAlignment.Center);
                            Place(v.Value, r.value, HorizontalAlignment.Right, VerticalAlignment.Center);
                            if (i < g.Items.Count - 1)   // divider line, except under the last row
                            {
                                var bb = ThemeManager.ParseColor(t.Color.BarBackground);
                                Box(new Rect(r.bounds.X + 5, r.bounds.Y + r.bounds.Height - 1, Math.Max(0, r.bounds.Width - 10), 1), 0,
                                    Brush(Color.FromArgb(100, bb.R, bb.G, bb.B)));
                            }
                            break;

                        default:
                            v.Label = Text(label, t.Font.Item, false, primary);
                            v.Value = Text(it.GetFormattedText(false), t.Font.Value, true, stateColor);
                            Place(v.Label, r.label, HorizontalAlignment.Left, VerticalAlignment.Center);
                            Place(v.Value, r.value, HorizontalAlignment.Right, VerticalAlignment.Center);
                            if (!r.bar.IsEmpty && r.bar.Width > 0 && r.bar.Height > 0)
                            {
                                Box(r.bar, r.bar.Height / 2, barBg);
                                v.BarWidth = r.bar.Width;
                                v.BarFill = Box(new Rect(r.bar.X, r.bar.Y, 0, r.bar.Height), r.bar.Height / 2, Brush(UIUtils.GetStateColor(it.CachedColorState, t, false)));
                            }
                            break;
                    }
                    _visuals[it] = v;
                }
            }
            Update();
            return new Size(L.Width, height);
        }

        // ------------------------------------------------------------------ per-tick update
        public void Update()
        {
            var t = T;
            bool narrow = t.Layout.Width < 240 * (t.Layout.LayoutScale <= 0 ? 1 : t.Layout.LayoutScale);
            foreach (var (it, v) in _visuals)
            {
                string text = it.Style == MetricRenderStyle.TwoColumn ? it.GetFormattedText(narrow) : it.GetFormattedText(false);
                if (v.Value.Text != text) v.Value.Text = text;
                var b = Brush(it.GetTextColor(t));
                if (!ReferenceEquals(v.Value.Foreground, b)) v.Value.Foreground = b;

                string label = string.IsNullOrEmpty(it.Label) ? it.Key : it.Label;
                if (v.Label.Text != label) v.Label.Text = label;

                if (v.BarFill != null)
                {
                    double pct = it.CachedPercent;
                    double w = v.BarWidth * pct;
                    if (w < 1 && pct > 0) w = 1;
                    v.BarFill.Width = Math.Max(0, w);
                    v.BarFill.CornerRadius = new CornerRadius(Math.Min(v.BarFill.Height / 2, w / 2));
                    var bb = Brush(UIUtils.GetStateColor(it.CachedColorState, t, false));
                    if (!ReferenceEquals(v.BarFill.Background, bb)) v.BarFill.Background = bb;
                }
            }
        }
    }
}
