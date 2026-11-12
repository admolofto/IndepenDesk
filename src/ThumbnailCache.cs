using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace IndepenDesk;

/// <summary>
/// Gizli pencereler için son görüntü önbelleği.
/// DWM canlı küçük resimleri yalnızca görünür pencereler için çalışır; IndepenDesk aktif olmayan
/// masaüstlerindeki pencereleri SW_HIDE ile gizlediği için genel bakış bu pencereleri canlı
/// gösteremez. Bu yüzden her pencere gizlenmeden hemen önce PrintWindow ile küçültülmüş bir
/// görüntüsü alınır ve genel bakışta canlı küçük resim yerine bu görüntü kullanılır.
/// Görünür pencereler de arka planda, düşük hızda (tick başına en fazla bir pencere) tazelenir.
/// </summary>
internal static class ThumbnailCache
{
    /// <summary>Saklanan görüntünün en uzun kenarı (piksel). Bellek / netlik dengesi.</summary>
    private const int MaxEdge = 560;

    /// <summary>Bu süreden yeni bir görüntü varsa gizlemeden önce tekrar yakalanmaz
    /// (masaüstü geçişlerini hızlı tutmak için).</summary>
    private const long FreshMs = 3000;

    /// <summary>Arka planda görünür pencereler bu yaştan sonra tazelenir.</summary>
    private const long BackgroundRefreshMs = 20_000;
    private const long SlowRefreshMs = 120_000;

    /// <summary>Bundan uzun süren yakalamalar "yavaş" sayılır; gizleme anında tekrar denenmez.</summary>
    private const long SlowCaptureMs = 120;

    private sealed class Entry
    {
        public required Bitmap Image;
        public long Taken;
    }

    private static readonly Dictionary<IntPtr, Entry> _map = new();
    private static readonly HashSet<IntPtr> _slow = new();
    private static int _tick;

    public static Bitmap? Get(IntPtr h) => _map.TryGetValue(h, out var e) ? e.Image : null;

    public static void Forget(IntPtr h)
    {
        if (_map.Remove(h, out var e)) e.Image.Dispose();
        _slow.Remove(h);
    }

    /// <summary>Pencere gizlenmeden hemen önce çağrılır. Görüntü tazeyse veya pencere yavaşsa atlanır.</summary>
    public static void CaptureBeforeHide(IntPtr h)
    {
        if (_slow.Contains(h)) return;
        if (_map.TryGetValue(h, out var e) && Environment.TickCount64 - e.Taken < FreshMs) return;
        Capture(h);
    }

    /// <summary>Arka plan tazeleme: görünür pencerelerden en eski görüntüye sahip olanı (en fazla bir tane) yakalar.</summary>
    public static void BackgroundTick(IEnumerable<IntPtr> visibleWindows)
    {
        if (++_tick % 20 == 0)
            foreach (var dead in _map.Keys.Where(h => !Native.IsWindow(h)).ToList())
                Forget(dead);

        long now = Environment.TickCount64;
        IntPtr best = IntPtr.Zero;
        long bestAge = 0;
        foreach (var h in visibleWindows)
        {
            if (Native.IsIconic(h)) continue;
            long age = _map.TryGetValue(h, out var e) ? now - e.Taken : long.MaxValue;
            long due = _slow.Contains(h) ? SlowRefreshMs : BackgroundRefreshMs;
            if (age >= due && age > bestAge)
            {
                best = h;
                bestAge = age;
            }
        }
        if (best != IntPtr.Zero) Capture(best);
    }

    /// <summary>Pencerenin görünen çerçevesini PrintWindow ile yakalar ve küçültüp saklar.</summary>
    public static bool Capture(IntPtr h)
    {
        try
        {
            if (!Native.IsWindow(h) || !Native.IsWindowVisible(h) || Native.IsIconic(h) || Native.IsHungAppWindow(h))
                return false;
            if (!Native.GetWindowRect(h, out var wr)) return false;
            var win = wr.ToRectangle();
            var frame = Native.GetVisibleFrame(h) ?? win;
            if (win.Width <= 0 || win.Height <= 0 || win.Width > 16384 || win.Height > 16384) return false;

            var sw = Stopwatch.StartNew();
            using var full = new Bitmap(win.Width, win.Height, PixelFormat.Format32bppRgb);
            bool ok;
            using (var g = Graphics.FromImage(full))
            {
                IntPtr hdc = g.GetHdc();
                try { ok = Native.PrintWindow(h, hdc, Native.PW_RENDERFULLCONTENT); }
                finally { g.ReleaseHdc(hdc); }
            }
            if (sw.ElapsedMilliseconds > SlowCaptureMs) _slow.Add(h); else _slow.Remove(h);
            if (!ok) return false;

            // Görünmez kenarlıkları kırp
            var crop = Rectangle.Intersect(
                new Rectangle(frame.Left - win.Left, frame.Top - win.Top, frame.Width, frame.Height),
                new Rectangle(0, 0, win.Width, win.Height));
            if (crop.Width < 8 || crop.Height < 8) return false;
            if (LooksBlank(full, crop)) return false; // bazı GPU/oyun pencereleri siyah döner

            double scale = Math.Min(1.0, MaxEdge / (double)Math.Max(crop.Width, crop.Height));
            int tw = Math.Max(1, (int)(crop.Width * scale));
            int th = Math.Max(1, (int)(crop.Height * scale));
            var small = new Bitmap(tw, th, PixelFormat.Format32bppRgb);
            using (var g = Graphics.FromImage(small))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(full, new Rectangle(0, 0, tw, th), crop, GraphicsUnit.Pixel);
            }

            if (_map.TryGetValue(h, out var old)) old.Image.Dispose();
            _map[h] = new Entry { Image = small, Taken = Environment.TickCount64 };
            return true;
        }
        catch
        {
            return false; // yakalama en iyi çabadır
        }
    }

    /// <summary>Görüntünün tek renk (genellikle siyah) olup olmadığını seyrek örneklemeyle kontrol eder.</summary>
    private static bool LooksBlank(Bitmap bmp, Rectangle area)
    {
        Color first = bmp.GetPixel(area.Left + area.Width / 2, area.Top + area.Height / 2);
        for (int i = 1; i <= 7; i++)
            for (int j = 1; j <= 7; j++)
            {
                var c = bmp.GetPixel(area.Left + area.Width * i / 8, area.Top + area.Height * j / 8);
                if (c.ToArgb() != first.ToArgb()) return false;
            }
        return true;
    }
}
