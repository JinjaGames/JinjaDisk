using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using JinjaDisk.Core;

namespace JinjaDisk.UI;

/// <summary>
/// Kare-oranlı (squarified) ağaç haritası. Her dikdörtgen bir dosya/klasör; alan = diskte kapladığı yer.
/// İç içe 3 seviyeye kadar klasör gösterir. Ana çizim ve vurgulama ayrı görsel katmanlarda.
/// </summary>
public sealed class TreemapControl : FrameworkElement
{
    struct Cell
    {
        public Rect R;
        public int Index;
        public bool IsDir;
        public int Depth;
    }

    readonly DrawingVisual main = new(), overlay = new();
    readonly List<Cell> cells = new();
    FileIndex idx;
    int root = -1;
    int hover = -1, selected = -1;
    bool dirty = true;
    const int MaxDepth = 3;
    const int MaxCells = 9000;

    static readonly Brush[] catFill = new Brush[10];
    static readonly Brush dirFill, dirHeader, borderBrush;
    static readonly Pen cellPen, hoverPen, selPen;
    static readonly Typeface face = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    static readonly Typeface faceLight = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    public event Action<int> ItemHovered;
    public event Action<int> ItemClicked;
    public event Action<int> ItemOpened;

    static TreemapControl()
    {
        for (int i = 0; i < 10; i++)
        {
            var c = ToColor(Categories.Colors[i]);
            var g = new LinearGradientBrush(Lighten(c, 0.22), Darken(c, 0.35), new Point(0, 0), new Point(1, 1));
            g.Freeze();
            catFill[i] = g;
        }
        dirFill = Frozen(new SolidColorBrush(Color.FromRgb(0x14, 0x18, 0x21)));
        dirHeader = Frozen(new SolidColorBrush(Color.FromRgb(0x1E, 0x24, 0x33)));
        borderBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x0D, 0x0F, 0x14)));
        cellPen = new Pen(borderBrush, 1); cellPen.Freeze();
        hoverPen = new Pen(Brushes.White, 2); hoverPen.Freeze();
        selPen = new Pen(new SolidColorBrush(Color.FromRgb(0x22, 0xD3, 0xEE)), 2.5); selPen.Freeze();
    }

    static Brush Frozen(Brush b) { b.Freeze(); return b; }
    public static Color ToColor(uint argb) => Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
    static Color Lighten(Color c, double f) => Color.FromRgb((byte)(c.R + (255 - c.R) * f), (byte)(c.G + (255 - c.G) * f), (byte)(c.B + (255 - c.B) * f));
    static Color Darken(Color c, double f) => Color.FromRgb((byte)(c.R * (1 - f)), (byte)(c.G * (1 - f)), (byte)(c.B * (1 - f)));

    public TreemapControl()
    {
        AddVisualChild(main);
        AddVisualChild(overlay);
        ClipToBounds = true;
        Focusable = true;
    }

    protected override int VisualChildrenCount => 2;
    protected override Visual GetVisualChild(int index) => index == 0 ? main : overlay;

    public int RootItem => root;

    public void SetRoot(FileIndex index, int item)
    {
        idx = index;
        root = item;
        hover = -1;
        dirty = true;
        InvalidateVisual();
    }

    public void SetSelected(int item)
    {
        selected = item;
        DrawOverlay();
    }

    public void Refresh() { dirty = true; InvalidateVisual(); }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        dirty = true;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize)); // fare olayları için
        if (dirty) { Rebuild(); dirty = false; }
    }

    void Rebuild()
    {
        cells.Clear();
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        using (var dc = main.RenderOpen())
        {
            var area = new Rect(0, 0, ActualWidth, ActualHeight);
            if (idx == null || root < 0 || area.Width < 4 || area.Height < 4) { DrawOverlay(); return; }
            if (!idx.IsDir(root))
            {
                AddCell(dc, root, area, 0, dpi);
            }
            else Layout(dc, root, area, 0, dpi);
        }
        DrawOverlay();
    }

    void Layout(DrawingContext dc, int node, Rect r, int depth, double dpi)
    {
        if (cells.Count > MaxCells) return;
        long total = 0;
        var kids = new List<int>();
        for (int c = idx.FirstChild[node]; c >= 0; c = idx.NextSibling[c])
        {
            if (idx.IsDeleted(c)) continue;
            long w = Weight(c);
            if (w <= 0) continue;
            kids.Add(c);
            total += w;
        }
        if (total <= 0 || kids.Count == 0) return;
        kids.Sort((a, b) => Weight(b).CompareTo(Weight(a)));

        double areaTotal = r.Width * r.Height;
        var items = new List<(int idx, double area)>(kids.Count);
        foreach (var k in kids)
        {
            double a = (double)Weight(k) / total * areaTotal;
            if (a < 6) break; // görünmeyecek kadar küçük: geri kalan da küçük (sıralı)
            items.Add((k, a));
        }
        if (items.Count == 0) return;

        Squarify(items, r, (k, rect) =>
        {
            if (cells.Count > MaxCells) return;
            AddCell(dc, k, rect, depth, dpi);
        });
    }

    long Weight(int i) => Math.Max(idx.Alloc[i], idx.Size[i] > 0 && idx.Alloc[i] == 0 ? 0 : idx.Alloc[i]);

    void AddCell(DrawingContext dc, int k, Rect rect, int depth, double dpi)
    {
        bool dir = idx.IsDir(k);
        cells.Add(new Cell { R = rect, Index = k, IsDir = dir, Depth = depth });
        if (!dir)
        {
            dc.DrawRectangle(catFill[(int)Categories.Of(idx, k)], cellPen, rect);
            if (rect.Width > 70 && rect.Height > 22) DrawLabel(dc, idx.Name(k), rect, Brushes.Black, 11, dpi, true);
            return;
        }
        // Klasör: çerçeve + başlık + iç düzen
        dc.DrawRectangle(dirFill, cellPen, rect);
        bool canNest = depth < MaxDepth - 1 && rect.Width > 26 && rect.Height > 26;
        double header = rect.Height > 44 && rect.Width > 50 ? 17 : 0;
        if (header > 0)
        {
            dc.DrawRectangle(dirHeader, null, new Rect(rect.X + 1, rect.Y + 1, rect.Width - 2, header - 1));
            DrawLabel(dc, idx.Name(k) + "  " + Fmt.Size(idx.Alloc[k]), new Rect(rect.X, rect.Y, rect.Width, header), Brushes.Gainsboro, 11, dpi, false);
        }
        if (canNest)
        {
            var inner = new Rect(rect.X + 2, rect.Y + header + 2, Math.Max(0, rect.Width - 4), Math.Max(0, rect.Height - header - 4));
            if (inner.Width > 3 && inner.Height > 3) Layout(dc, k, inner, depth + 1, dpi);
        }
        else
        {
            // En derin seviye: klasörün baskın türüne göre tek blok
            var inner = new Rect(rect.X + 2, rect.Y + header + 2, Math.Max(0, rect.Width - 4), Math.Max(0, rect.Height - header - 4));
            if (inner.Width > 2 && inner.Height > 2)
            {
                dc.DrawRectangle(catFill[(int)DominantCategory(k)], null, inner);
            }
        }
    }

    Category DominantCategory(int dir)
    {
        // Doğrudan çocuklardan en büyüğünün türü (hızlı yaklaşım)
        int best = -1; long bw = -1;
        for (int c = idx.FirstChild[dir]; c >= 0; c = idx.NextSibling[c])
        {
            if (idx.IsDeleted(c)) continue;
            if (idx.Alloc[c] > bw) { bw = idx.Alloc[c]; best = c; }
        }
        if (best < 0) return Category.Other;
        return idx.IsDir(best) ? DominantCategory(best) : Categories.Of(idx, best);
    }

    void DrawLabel(DrawingContext dc, string text, Rect r, Brush brush, double size, double dpi, bool center)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, center ? faceLight : face, size, brush, dpi)
        {
            MaxTextWidth = Math.Max(1, r.Width - 8),
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis
        };
        double x = r.X + 5;
        double y = center ? r.Y + (r.Height - ft.Height) / 2 : r.Y + (r.Height - ft.Height) / 2;
        if (center) x = r.X + Math.Max(4, (r.Width - ft.WidthIncludingTrailingWhitespace) / 2);
        dc.DrawText(ft, new Point(x, y));
    }

    static void Squarify(List<(int idx, double area)> items, Rect r, Action<int, Rect> emit)
    {
        int i = 0;
        while (i < items.Count && r.Width > 0.5 && r.Height > 0.5)
        {
            double side = Math.Min(r.Width, r.Height);
            int j = i;
            double sum = 0, worst = double.MaxValue;
            while (j < items.Count)
            {
                double s = sum + items[j].area;
                double w = Worst(items, i, j, s, side);
                if (j > i && w > worst) break;
                worst = w; sum = s; j++;
            }
            if (r.Width >= r.Height)
            {
                double cw = Math.Min(r.Width, sum / r.Height);
                double y = r.Y;
                for (int k = i; k < j; k++)
                {
                    double h = items[k].area / cw;
                    emit(items[k].idx, new Rect(r.X, y, cw, h));
                    y += h;
                }
                r = new Rect(r.X + cw, r.Y, Math.Max(0, r.Width - cw), r.Height);
            }
            else
            {
                double rh = Math.Min(r.Height, sum / r.Width);
                double x = r.X;
                for (int k = i; k < j; k++)
                {
                    double w = items[k].area / rh;
                    emit(items[k].idx, new Rect(x, r.Y, w, rh));
                    x += w;
                }
                r = new Rect(r.X, r.Y + rh, r.Width, Math.Max(0, r.Height - rh));
            }
            i = j;
        }
    }

    static double Worst(List<(int idx, double area)> items, int i, int j, double sum, double side)
    {
        double max = 0, min = double.MaxValue;
        for (int k = i; k <= j; k++)
        {
            double a = items[k].area;
            if (a > max) max = a;
            if (a < min) min = a;
        }
        double s2 = sum * sum, w2 = side * side;
        return Math.Max(w2 * max / s2, s2 / (w2 * min));
    }

    void DrawOverlay()
    {
        using var dc = overlay.RenderOpen();
        foreach (var c in cells)
        {
            if (c.Index == selected) dc.DrawRectangle(null, selPen, Deflate(c.R, 1.25));
        }
        if (hover >= 0 && hover < cells.Count)
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), hoverPen, Deflate(cells[hover].R, 1));
    }

    static Rect Deflate(Rect r, double d) =>
        new(r.X + d, r.Y + d, Math.Max(0, r.Width - 2 * d), Math.Max(0, r.Height - 2 * d));

    int HitTestCell(Point p)
    {
        // En derin (en son eklenen) hücre önce
        for (int k = cells.Count - 1; k >= 0; k--)
            if (cells[k].R.Contains(p)) return k;
        return -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int h = HitTestCell(e.GetPosition(this));
        if (h != hover)
        {
            hover = h;
            DrawOverlay();
            ItemHovered?.Invoke(h >= 0 ? cells[h].Index : -1);
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        hover = -1;
        DrawOverlay();
        ItemHovered?.Invoke(-1);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        int h = HitTestCell(e.GetPosition(this));
        if (h < 0) return;
        int item = cells[h].Index;
        if (e.ClickCount == 2)
        {
            // Çift tık: klasöre gir (dosyaysa üst klasörüne)
            int target = idx.IsDir(item) ? item : idx.Parent[item];
            // Kökün doğrudan altındaki seviyeye in
            int c = target;
            while (c >= 0 && idx.Parent[c] != root && c != root) c = idx.Parent[c];
            ItemOpened?.Invoke(c >= 0 && idx.IsDir(c) ? c : target);
        }
        else
        {
            ItemClicked?.Invoke(item);
        }
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        int h = HitTestCell(e.GetPosition(this));
        if (h >= 0) ItemClicked?.Invoke(cells[h].Index);
    }
}

/// <summary>Kategori dağılımı için halka grafik.</summary>
public sealed class DonutChart : FrameworkElement
{
    public readonly List<(double value, Color color)> Slices = new();
    public string CenterTitle = "", CenterSub = "";

    public void Update() => InvalidateVisual();

    protected override void OnRender(DrawingContext dc)
    {
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size < 20) return;
        var c = new Point(ActualWidth / 2, ActualHeight / 2);
        double rOuter = size / 2 - 2, thick = Math.Max(14, size * 0.13), rMid = rOuter - thick / 2;
        double total = 0;
        foreach (var s in Slices) total += s.value;
        var track = new Pen(new SolidColorBrush(Color.FromRgb(0x23, 0x29, 0x38)), thick);
        dc.DrawEllipse(null, track, c, rMid, rMid);
        if (total > 0)
        {
            double angle = -90;
            foreach (var (value, color) in Slices)
            {
                double sweep = value / total * 360;
                if (sweep < 0.4) { angle += sweep; continue; }
                double gap = Slices.Count > 1 ? Math.Min(1.2, sweep / 3) : 0;
                DrawArc(dc, c, rMid, angle + gap / 2, sweep - gap, new Pen(new SolidColorBrush(color), thick) { StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Flat });
                angle += sweep;
            }
        }
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var tf = new Typeface(new FontFamily("Segoe UI Variable Display, Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        var t1 = new FormattedText(CenterTitle, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, tf, size * 0.11,
            new SolidColorBrush(Color.FromRgb(0xE8, 0xEA, 0xF0)), dpi);
        var t2 = new FormattedText(CenterSub, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), Math.Max(10, size * 0.055), new SolidColorBrush(Color.FromRgb(0x8D, 0x95, 0xA8)), dpi);
        dc.DrawText(t1, new Point(c.X - t1.Width / 2, c.Y - t1.Height / 2 - t2.Height / 2));
        dc.DrawText(t2, new Point(c.X - t2.Width / 2, c.Y + t1.Height / 2 - t2.Height / 2));
    }

    static void DrawArc(DrawingContext dc, Point c, double r, double startDeg, double sweepDeg, Pen pen)
    {
        if (sweepDeg >= 359.9) { dc.DrawEllipse(null, pen, c, r, r); return; }
        double a0 = startDeg * Math.PI / 180, a1 = (startDeg + sweepDeg) * Math.PI / 180;
        var p0 = new Point(c.X + r * Math.Cos(a0), c.Y + r * Math.Sin(a0));
        var p1 = new Point(c.X + r * Math.Cos(a1), c.Y + r * Math.Sin(a1));
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(p0, false, false);
            ctx.ArcTo(p1, new Size(r, r), 0, sweepDeg > 180, SweepDirection.Clockwise, true, false);
        }
        g.Freeze();
        dc.DrawGeometry(null, pen, g);
    }
}
