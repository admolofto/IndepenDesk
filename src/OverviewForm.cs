using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace IndepenDesk;

/// <summary>
/// Genel bakış (Mission Control benzeri): tüm monitörler, masaüstleri ve pencereler.
///
/// Her pencere bir önizleme kutucuğu olarak gösterilir:
///  • görünür pencereler (aktif masaüstlerindekiler) için canlı DWM küçük resmi,
///  • gizli pencereler (diğer masaüstleri) için gizlenmeden önce yakalanan son görüntü
///    (<see cref="ThumbnailCache"/>), o da yoksa uygulama simgesi.
///
/// Tüm arayüz tek bir yüzeye elle çizilir. DWM küçük resimleri pencerenin GDI içeriğinin
/// üstüne bindirildiği için kaydırma alanının dışına taşan kısımları kaynak dikdörtgeni
/// kırpılarak gizlenir. Sürükle-bırak OLE yerine fare yakalamayla yapılır; sürüklenen
/// pencerenin canlı küçük resmi imleci izleyen etkinleşmeyen bir "hayalet" pencerede gösterilir.
///
/// Etkileşimler: pencereye tıkla → ona git • pencereyi başka masaüstüne veya "+" kartına
/// (yeni masaüstü) sürükle • masaüstü başlığını başka monitöre sürükle • orta tık / × → kapat
/// • sağ tık → menü • yazarak ara, ↑/↓/Tab ile seç, Enter ile git • Esc → kapat.
/// </summary>
internal sealed class OverviewForm : Form
{
    private static OverviewForm? _open;
    public static bool IsOpen => _open != null;

    public static void Toggle(DesktopManager mgr)
    {
        if (_open != null) { _open.SafeClose(); return; }
        var f = new OverviewForm(mgr);
        _open = f;
        f.FormClosed += (_, _) => { if (_open == f) _open = null; };
        f.Show();
        f.Activate();
    }

    // ---------- renk paleti ----------
    private static readonly Color BgColor = Color.FromArgb(23, 23, 27);
    private static readonly Color HeaderBg = Color.FromArgb(28, 28, 33);
    private static readonly Color CardBg = Color.FromArgb(36, 36, 43);
    private static readonly Color CardBorder = Color.FromArgb(58, 58, 68);
    private static readonly Color CardBorderHover = Color.FromArgb(92, 92, 108);
    private static readonly Color ThumbBg = Color.FromArgb(28, 28, 34);
    private static readonly Color Accent = Color.FromArgb(75, 141, 224);     // aktif masaüstü / seçim
    private static readonly Color DropAccent = Color.FromArgb(70, 170, 110); // bırakma hedefi
    private static readonly Color CloseRed = Color.FromArgb(214, 64, 64);
    private static readonly Color TextMain = Color.FromArgb(228, 228, 236);
    private static readonly Color TextDim = Color.FromArgb(150, 150, 162);
    private static readonly Color TextFaint = Color.FromArgb(105, 105, 118);

    // ---------- model ----------
    private sealed class Section
    {
        public required MonitorEntry Mon;
        public readonly List<Card> Cards = new();
        public AddSlot? Add;
        public Rectangle Bounds, Label;
    }

    private sealed class Card
    {
        public required Section Section;
        public required DesktopEntry Desk;
        public readonly List<Tile> Tiles = new();
        public int FilteredOut; // aramayla gizlenen pencere sayısı
        public Rectangle Bounds, Header;
        public MonitorEntry Mon => Section.Mon;
    }

    private sealed class AddSlot
    {
        public required Section Section;
        public Rectangle Bounds;
    }

    private sealed class Tile
    {
        public required WindowEntry Win;
        public required Card Card;
        public Rectangle Bounds, Thumb, Caption, CloseBtn; // içerik koordinatları
        public IntPtr Live;            // DWM küçük resim tanıtıcısı (0: canlı değil)
        public Rectangle Source;       // canlı küçük resim için kaynak dikdörtgeni
        public Rectangle Drawn;        // küçük resmin son çizildiği yer (istemci koordinatları)
        public bool Minimized;
        public IntPtr Handle => Win.Handle;
    }

    private readonly DesktopManager _mgr;
    private readonly List<Section> _sections = new();
    private readonly List<Tile> _tiles = new(); // görsel sırada
    private readonly Dictionary<IntPtr, IntPtr> _thumbs = new(); // kaynak pencere → DWM küçük resmi
    private readonly Dictionary<IntPtr, (Icon? Small, Icon? Big)> _icons = new();
    private readonly Dictionary<uint, string> _procNames = new();
    private readonly TextBox _search;
    private readonly ToolTip _tips = new() { InitialDelay = 500, ReshowDelay = 200 };
    private readonly System.Windows.Forms.Timer _refresh = new() { Interval = 500 };
    private readonly Font _titleFont = new("Segoe UI", 15f, FontStyle.Bold);
    private readonly Font _legendFont = new("Segoe UI", 9f);
    private readonly Font _sectionFont = new("Segoe UI", 12f, FontStyle.Bold);
    private readonly Font _cardFont = new("Segoe UI", 10.5f, FontStyle.Bold);
    private readonly Font _smallFont = new("Segoe UI", 8.5f);
    private readonly Font _badgeFont = new("Segoe UI", 7.5f, FontStyle.Bold);
    private readonly Font _captionFont = new("Segoe UI", 9f);
    private readonly Font _plusFont = new("Segoe UI Light", 26f);

    private IReadOnlyList<MonitorEntry> _layout = Array.Empty<MonitorEntry>();
    private string _signature = "";
    private float _k = 1f;   // yerleşim ölçeği (ekrana sığdırmak için)
    private int _scrollY, _contentH;
    private bool _menuOpen;
    private ContextMenuStrip? _menu;

    // fare / klavye durumu
    private object? _hover;
    private bool _hoverClose;
    private Tile? _selected;
    private object? _downTarget;
    private bool _downOnHeader, _downOnClose;
    private Point _downPt;
    private MouseButtons _downBtn;
    private bool _dragging;
    private DragGhost? _ghost;
    private object? _dropTarget;

    private float Dpi => DeviceDpi / 96f;
    private int U(float v) => (int)Math.Round(v * Dpi);        // sabit arayüz ölçüsü
    private int K(float v) => (int)Math.Round(v * Dpi * _k);   // ölçeklenen yerleşim ölçüsü
    private int HeaderH => U(92);
    private Rectangle Viewport => new(0, HeaderH, ClientSize.Width, Math.Max(0, ClientSize.Height - HeaderH));

    private OverviewForm(DesktopManager mgr)
    {
        _mgr = mgr;

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        BackColor = BgColor;
        ShowInTaskbar = false;
        TopMost = true;
        KeyPreview = true;
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

        Native.GetCursorPos(out var pt);
        var screen = Screen.AllScreens.FirstOrDefault(s => s.Bounds.Contains(pt.X, pt.Y)) ?? Screen.PrimaryScreen!;
        Bounds = screen.WorkingArea;

        _search = new TextBox
        {
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Color.FromArgb(44, 44, 52),
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 11f),
            PlaceholderText = L.T("ov.search")
        };
        _search.TextChanged += (_, _) =>
        {
            _scrollY = 0;
            Rebuild();
            _selected = _search.Text.Length > 0 ? _tiles.FirstOrDefault() : null;
            Invalidate();
        };
        Controls.Add(_search);

        Deactivate += (_, _) => { if (!_menuOpen) SafeClose(); };
        _refresh.Tick += (_, _) => AutoRefresh();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        PositionSearch();
        Reload();
        _refresh.Start();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _search.Focus();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (!IsHandleCreated) return;
        PositionSearch();
        Relayout();
    }

    private void PositionSearch()
    {
        int w = Math.Min(U(340), ClientSize.Width / 3);
        _search.Width = w;
        _search.Location = new Point(ClientSize.Width - w - U(28), U(20));
    }

    private void SafeClose()
    {
        if (!IsDisposed && !Disposing) Close();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _refresh.Stop();
        CancelDrag();
        foreach (var t in _thumbs.Values) Native.DwmUnregisterThumbnail(t);
        _thumbs.Clear();
        foreach (var (s, b) in _icons.Values) { s?.Dispose(); b?.Dispose(); }
        _icons.Clear();
        base.OnFormClosed(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _refresh.Dispose();
            _tips.Dispose();
            _ghost?.Dispose();
            _menu?.Dispose();
            foreach (var f in new[] { _titleFont, _legendFont, _sectionFont, _cardFont, _smallFont, _badgeFont, _captionFont, _plusFont })
                f.Dispose();
        }
        base.Dispose(disposing);
    }

    // =====================================================================
    // Model oluşturma
    // =====================================================================

    /// <summary>Durumu yöneticiden yeniden okur ve her şeyi yeniden kurar.</summary>
    private void Reload()
    {
        _layout = _mgr.GetLayout();
        _signature = Signature(_layout);
        Rebuild();
    }

    /// <summary>Açıkken pencere açılıp kapanırsa veya başlıklar değişirse görünümü tazeler.</summary>
    private void AutoRefresh()
    {
        if (_dragging || _menuOpen || _downTarget != null || !Visible) return;
        var layout = _mgr.GetLayout();
        string sig = Signature(layout);
        if (sig == _signature) return;
        _layout = layout;
        _signature = sig;
        Rebuild();
    }

    private static string Signature(IReadOnlyList<MonitorEntry> layout)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var m in layout)
        {
            sb.Append('M').Append(m.Device);
            foreach (var d in m.Desktops)
            {
                sb.Append('D').Append(d.IsCurrent ? '*' : '-');
                foreach (var w in d.Windows)
                    sb.Append(w.Handle).Append(Native.IsWindowVisible(w.Handle) ? 'v' : 'h')
                      .Append(Native.IsIconic(w.Handle) ? 'm' : 'n').Append(w.Title).Append('\u0001');
            }
        }
        return sb.ToString();
    }

    /// <summary>Mevcut düzenden (ve arama filtresinden) bölüm/kart/kutucuk modelini kurar,
    /// canlı küçük resimleri kaydeder ve yerleşimi hesaplar.</summary>
    private void Rebuild()
    {
        string q = _search.Text.Trim();
        IntPtr selectedHandle = _selected?.Handle ?? IntPtr.Zero;
        _sections.Clear();
        _tiles.Clear();
        _hover = null;
        _selected = null;

        foreach (var mon in _layout)
        {
            var sec = new Section { Mon = mon };
            foreach (var desk in mon.Desktops)
            {
                var card = new Card { Section = sec, Desk = desk };
                foreach (var win in desk.Windows)
                {
                    if (q.Length > 0 && !Matches(win, q)) { card.FilteredOut++; continue; }
                    var tile = new Tile { Win = win, Card = card, Minimized = Native.IsIconic(win.Handle) };
                    card.Tiles.Add(tile);
                    _tiles.Add(tile);
                    if (tile.Handle == selectedHandle) _selected = tile;
                }
                sec.Cards.Add(card);
            }
            if (mon.Desktops.Count < DesktopManager.MaxDesktopsPerMonitor)
                sec.Add = new AddSlot { Section = sec };
            _sections.Add(sec);
        }

        SyncThumbnails();
        Relayout();
    }

    private bool Matches(WindowEntry w, string q)
    {
        if (w.Title.Contains(q, StringComparison.CurrentCultureIgnoreCase)) return true;
        return ProcessName(w.Handle).Contains(q, StringComparison.CurrentCultureIgnoreCase);
    }

    private string ProcessName(IntPtr h)
    {
        Native.GetWindowThreadProcessId(h, out uint pid);
        if (_procNames.TryGetValue(pid, out var name)) return name;
        try { name = Process.GetProcessById((int)pid).ProcessName; }
        catch { name = ""; }
        _procNames[pid] = name;
        return name;
    }

    private (Icon? Small, Icon? Big) IconsOf(IntPtr h)
    {
        if (!_icons.TryGetValue(h, out var icons))
        {
            icons = (Native.GetWindowIcon(h, big: false), Native.GetWindowIcon(h, big: true));
            _icons[h] = icons;
        }
        return icons;
    }

    /// <summary>Görünür pencereler için DWM küçük resmi kaydeder, artık gerekmeyenleri bırakır.</summary>
    private void SyncThumbnails()
    {
        var wanted = new HashSet<IntPtr>();
        foreach (var t in _tiles)
        {
            IntPtr h = t.Handle;
            if (!Native.IsWindowVisible(h)) continue; // gizli pencerelerin canlı küçük resmi boş çıkar

            if (!_thumbs.TryGetValue(h, out var thumb))
            {
                if (Native.DwmRegisterThumbnail(Handle, h, out thumb) != 0) continue;
                _thumbs[h] = thumb;
            }
            if (Native.DwmQueryThumbnailSourceSize(thumb, out var size) != 0 || size.cx < 16 || size.cy < 16)
                continue;
            // Simge durumundaki pencere yalnızca başlık çubuğu boyutunu bildiriyorsa canlı küçük resim
            // işe yaramaz; varsa son yakalanan görüntü kullanılır
            if (t.Minimized && size.cy < 80 && ThumbnailCache.Get(h) != null)
                continue;

            // Kaynak: görünmez kenarlıklar hariç görünen çerçeve
            var src = new Rectangle(0, 0, size.cx, size.cy);
            if (!t.Minimized && Native.GetWindowRect(h, out var wr) && Native.GetVisibleFrame(h) is { } fr)
            {
                var vis = Rectangle.Intersect(src,
                    new Rectangle(fr.Left - wr.Left, fr.Top - wr.Top, fr.Width, fr.Height));
                if (vis.Width > 16 && vis.Height > 16) src = vis;
            }
            t.Live = thumb;
            t.Source = src;
            wanted.Add(h);
        }

        foreach (var h in _thumbs.Keys.Where(h => !wanted.Contains(h)).ToList())
        {
            Native.DwmUnregisterThumbnail(_thumbs[h]);
            _thumbs.Remove(h);
        }
    }

    // =====================================================================
    // Yerleşim
    // =====================================================================

    private static readonly float[] Scales = { 1.4f, 1.25f, 1.1f, 1f, 0.9f, 0.8f, 0.72f };

    /// <summary>Ekrana kaydırmasız sığan en büyük ölçeği seçer (sığmazsa en küçükle kaydırır).</summary>
    private void Relayout()
    {
        if (ClientSize.Width <= 0) return;
        int avail = Viewport.Height;
        int h = 0;
        foreach (float k in Scales)
        {
            h = LayoutAt(k);
            if (h <= avail) break;
        }
        _contentH = h;
        _scrollY = Math.Clamp(_scrollY, 0, Math.Max(0, _contentH - avail));
        UpdateThumbnails();
        Invalidate();
    }

    private static int ColumnsFor(int n) => n switch
    {
        <= 1 => 1,
        2 => 2,
        3 => 3,
        4 => 2,
        <= 9 => 3,
        <= 16 => 4,
        _ => (int)Math.Ceiling(Math.Sqrt(n))
    };

    /// <summary>Verilen ölçekte tüm dikdörtgenleri (içerik koordinatlarında) hesaplar; içerik yüksekliğini döndürür.</summary>
    private int LayoutAt(float k)
    {
        _k = k;
        int W = ClientSize.Width;
        int pad = U(28);
        int tileW = K(232), thumbH = K(140);
        int capH = Math.Max(U(24), K(28));
        int tileH = thumbH + capH;
        int gap = K(10), cardPad = K(10);
        int headH = U(36), cardGap = U(16), addW = U(104);
        int labelH = U(30), sectionGap = U(16);

        int maxCols = Math.Max(1, (W - 2 * pad - 2 * cardPad - addW - cardGap + gap) / (tileW + gap));
        int y = U(10);

        foreach (var sec in _sections)
        {
            sec.Label = new Rectangle(pad, y, W - 2 * pad, labelH);
            int sectionTop = y;
            y += labelH + U(6);

            int x = pad, lineTop = y, lineH = 0;
            int rightLimit = W - pad - (sec.Add != null ? addW + cardGap : 0);
            foreach (var card in sec.Cards)
            {
                int slots = Math.Max(1, card.Tiles.Count);
                int cols = Math.Min(maxCols, ColumnsFor(slots));
                int rows = (slots + cols - 1) / cols;
                int cw = 2 * cardPad + cols * tileW + (cols - 1) * gap;
                int ch = headH + rows * tileH + (rows - 1) * gap + cardPad;

                if (x > pad && x + cw > rightLimit)
                {
                    lineTop += lineH + cardGap;
                    x = pad;
                    lineH = 0;
                }

                card.Bounds = new Rectangle(x, lineTop, cw, ch);
                card.Header = new Rectangle(x, lineTop, cw, headH);
                for (int i = 0; i < card.Tiles.Count; i++)
                {
                    var t = card.Tiles[i];
                    int tx = x + cardPad + (i % cols) * (tileW + gap);
                    int ty = lineTop + headH + (i / cols) * (tileH + gap);
                    t.Bounds = new Rectangle(tx, ty, tileW, tileH);
                    t.Thumb = new Rectangle(tx, ty, tileW, thumbH);
                    t.Caption = new Rectangle(tx, ty + thumbH, tileW, capH);
                    t.CloseBtn = new Rectangle(tx + tileW - capH, ty + thumbH, capH, capH);
                }

                x += cw + cardGap;
                lineH = Math.Max(lineH, ch);
            }

            if (sec.Add != null)
            {
                int minH = headH + tileH + cardPad;
                sec.Add.Bounds = new Rectangle(x, lineTop, addW, Math.Max(lineH, minH));
                lineH = Math.Max(lineH, sec.Add.Bounds.Height);
            }

            y = lineTop + lineH;
            sec.Bounds = new Rectangle(pad - U(10), sectionTop - U(4), W - 2 * pad + U(20), y - sectionTop + U(14));
            y += sectionGap + U(10);
        }
        return y + pad;
    }

    private Rectangle ToClient(Rectangle r) => new(r.X, r.Y + HeaderH - _scrollY, r.Width, r.Height);
    private Point ToContent(Point p) => new(p.X, p.Y - HeaderH + _scrollY);

    private static Rectangle Fit(Rectangle area, Size src)
    {
        if (src.Width <= 0 || src.Height <= 0) return area;
        double s = Math.Min(area.Width / (double)src.Width, area.Height / (double)src.Height);
        int w = Math.Max(1, (int)(src.Width * s));
        int h = Math.Max(1, (int)(src.Height * s));
        return new Rectangle(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h);
    }

    private Rectangle ThumbContentArea(Tile t)
    {
        var r = t.Thumb;
        int inset = K(6);
        r.Inflate(-inset, -inset);
        return r;
    }

    /// <summary>Canlı küçük resimlerin hedef dikdörtgenlerini kaydırma ve görünüm alanına göre günceller.</summary>
    private void UpdateThumbnails()
    {
        var vp = Viewport;
        foreach (var t in _tiles)
        {
            if (t.Live == IntPtr.Zero) continue;
            var dest = Fit(ToClient(ThumbContentArea(t)), t.Source.Size);
            t.Drawn = dest;
            var vis = Rectangle.Intersect(dest, vp);

            var props = new Native.DWM_THUMBNAIL_PROPERTIES
            {
                dwFlags = Native.DWM_TNP_VISIBLE | Native.DWM_TNP_RECTDESTINATION |
                          Native.DWM_TNP_RECTSOURCE | Native.DWM_TNP_OPACITY |
                          Native.DWM_TNP_SOURCECLIENTAREAONLY,
                opacity = (byte)(_dragging && _downTarget == t ? 70 : 255),
                fSourceClientAreaOnly = false
            };
            if (vis.Width < 2 || vis.Height < 2)
            {
                props.fVisible = false;
            }
            else
            {
                // Görünüm alanı dışına taşan kısmı kaynak tarafında kırp
                double sx = t.Source.Width / (double)dest.Width;
                double sy = t.Source.Height / (double)dest.Height;
                props.fVisible = true;
                props.rcDestination = new Native.RECT(vis);
                props.rcSource = new Native.RECT(Rectangle.FromLTRB(
                    t.Source.Left + (int)((vis.Left - dest.Left) * sx),
                    t.Source.Top + (int)((vis.Top - dest.Top) * sy),
                    t.Source.Left + (int)Math.Ceiling((vis.Right - dest.Left) * sx),
                    t.Source.Top + (int)Math.Ceiling((vis.Bottom - dest.Top) * sy)));
            }
            Native.DwmUpdateThumbnailProperties(t.Live, ref props);
        }
    }

    private void ScrollBy(int dy)
    {
        int max = Math.Max(0, _contentH - Viewport.Height);
        int ny = Math.Clamp(_scrollY + dy, 0, max);
        if (ny == _scrollY) return;
        _scrollY = ny;
        UpdateThumbnails();
        Invalidate();
    }

    private void EnsureVisible(Tile t)
    {
        var r = ToClient(t.Bounds);
        var vp = Viewport;
        if (r.Top < vp.Top) ScrollBy(r.Top - vp.Top - U(12));
        else if (r.Bottom > vp.Bottom) ScrollBy(r.Bottom - vp.Bottom + U(12));
    }

    // =====================================================================
    // Çizim
    // =====================================================================

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        // Başlık alanı
        using (var hb = new SolidBrush(HeaderBg))
            g.FillRectangle(hb, 0, 0, ClientSize.Width, HeaderH);
        TextRenderer.DrawText(g, L.T("ov.title"), _titleFont, new Point(U(26), U(16)), Color.White);
        var legendRect = new Rectangle(U(28), U(56), Math.Max(10, _search.Left - U(48)), U(24));
        TextRenderer.DrawText(g, L.T("ov.legend"), _legendFont, legendRect, TextDim,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
        using (var line = new Pen(CardBorder))
            g.DrawLine(line, 0, HeaderH - 1, ClientSize.Width, HeaderH - 1);

        // İçerik (kaydırılan alan)
        var vp = Viewport;
        g.SetClip(vp);
        g.TranslateTransform(0, HeaderH - _scrollY);

        foreach (var sec in _sections)
        {
            PaintSection(g, sec);
            foreach (var card in sec.Cards) PaintCard(g, card);
            if (sec.Add != null) PaintAddSlot(g, sec.Add);
        }

        g.ResetTransform();
        g.ResetClip();

        // Kaydırma göstergesi
        if (_contentH > vp.Height)
        {
            int trackH = vp.Height - U(16);
            int thumbH = Math.Max(U(30), trackH * vp.Height / _contentH);
            int maxScroll = _contentH - vp.Height;
            int ty = vp.Top + U(8) + (trackH - thumbH) * _scrollY / Math.Max(1, maxScroll);
            using var sb = new SolidBrush(Color.FromArgb(90, 255, 255, 255));
            using var path = RoundedRect(new Rectangle(ClientSize.Width - U(9), ty, U(5), thumbH), U(2));
            g.FillPath(sb, path);
        }
    }

    private void PaintSection(Graphics g, Section sec)
    {
        // Monitör simgesi
        var lr = sec.Label;
        int iw = U(20), ih = U(14);
        var icon = new Rectangle(lr.Left + U(2), lr.Top + (lr.Height - ih) / 2 - U(2), iw, ih);
        using (var p = new Pen(TextMain, Math.Max(1f, 1.5f * Dpi)))
        {
            g.DrawRectangle(p, icon);
            g.DrawLine(p, icon.Left + iw / 2, icon.Bottom, icon.Left + iw / 2, icon.Bottom + U(3));
            g.DrawLine(p, icon.Left + iw / 4, icon.Bottom + U(4), icon.Right - iw / 4, icon.Bottom + U(4));
        }

        string name = L.F("ov.monitor", sec.Mon.Ordinal);
        var nameSize = TextRenderer.MeasureText(g, name, _sectionFont);
        TextRenderer.DrawText(g, name, _sectionFont, new Point(lr.Left + U(30), lr.Top + (lr.Height - nameSize.Height) / 2), Color.White);

        int windows = sec.Mon.Desktops.Sum(d => d.Windows.Count);
        string meta = L.F("ov.monitorMeta", sec.Mon.Desktops.Count, windows);
        TextRenderer.DrawText(g, meta, _smallFont,
            new Rectangle(lr.Left + U(30) + nameSize.Width + U(6), lr.Top, U(400), lr.Height), TextDim,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);

        if (_dropTarget == sec)
        {
            using var pen = new Pen(DropAccent, 2f * Dpi) { DashStyle = DashStyle.Dash };
            using var path = RoundedRect(sec.Bounds, U(12));
            g.DrawPath(pen, path);
            TextRenderer.DrawText(g, L.T("ov.dropDesktop"), _smallFont,
                new Rectangle(lr.Left, lr.Top, lr.Width, lr.Height), DropAccent,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }
    }

    private void PaintCard(Graphics g, Card card)
    {
        var b = card.Bounds;
        bool drop = _dropTarget == card;
        bool hover = _hover == card || (_hover is Tile ht && ht.Card == card);
        bool draggingThisDesktop = _dragging && _downTarget == card;

        using (var path = RoundedRect(b, U(10)))
        {
            using (var fill = new SolidBrush(drop ? Color.FromArgb(38, 52, 44) : CardBg))
                g.FillPath(fill, path);
            Color border = drop ? DropAccent : card.Desk.IsCurrent ? Accent : hover ? CardBorderHover : CardBorder;
            float width = drop ? 2.5f : card.Desk.IsCurrent ? 2f : 1.2f;
            using var pen = new Pen(border, width * Dpi);
            if (draggingThisDesktop) pen.DashStyle = DashStyle.Dash;
            g.DrawPath(pen, path);
        }

        // Başlık: tutamaç + "Masaüstü N" + pencere sayısı + AKTİF rozeti
        var h = card.Header;
        TextRenderer.DrawText(g, "⠿", _cardFont, new Rectangle(h.Left + U(8), h.Top, U(18), h.Height),
            _hover == card && _hoverHeader ? TextMain : TextFaint,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        string title = L.F("ov.desktopName", card.Desk.GlobalNumber);
        var ts = TextRenderer.MeasureText(g, title, _cardFont);
        int tx = h.Left + U(28);
        TextRenderer.DrawText(g, title, _cardFont, new Point(tx, h.Top + (h.Height - ts.Height) / 2), Color.White);

        int right = h.Right - U(10);
        if (card.Desk.IsCurrent)
        {
            string badge = L.T("ov.active");
            var bs = TextRenderer.MeasureText(g, badge, _badgeFont);
            var br = new Rectangle(right - bs.Width - U(8), h.Top + (h.Height - bs.Height - U(4)) / 2, bs.Width + U(8), bs.Height + U(4));
            using (var bb = new SolidBrush(Accent))
            using (var bp = RoundedRect(br, U(4)))
                g.FillPath(bb, bp);
            TextRenderer.DrawText(g, badge, _badgeFont, br, Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            right = br.Left - U(8);
        }

        string meta = drop ? L.T("ov.drop") : CountText(card.Desk.Windows.Count);
        int metaLeft = tx + ts.Width + U(4);
        if (right > metaLeft)
            TextRenderer.DrawText(g, meta, _smallFont, Rectangle.FromLTRB(metaLeft, h.Top, right, h.Bottom),
                drop ? DropAccent : TextDim,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);

        if (card.Tiles.Count == 0)
        {
            var area = Rectangle.FromLTRB(b.Left + K(10), h.Bottom, b.Right - K(10), b.Bottom - K(10));
            using (var pen = new Pen(CardBorder, 1f * Dpi) { DashStyle = DashStyle.Dash })
            using (var path = RoundedRect(area, U(8)))
                g.DrawPath(pen, path);
            string text = card.FilteredOut > 0 ? L.T("ov.noMatch") : L.T("ov.empty");
            TextRenderer.DrawText(g, text, _smallFont, area, TextFaint,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
        }

        foreach (var t in card.Tiles) PaintTile(g, t);
    }

    private bool _hoverHeader;

    private static string CountText(int n) => n == 1 ? L.T("ov.count1") : L.F("ov.count", n);

    private void PaintTile(Graphics g, Tile t)
    {
        bool hover = _hover == t;
        bool selected = _selected == t;
        bool dragged = _dragging && _downTarget == t;

        // Önizleme alanı
        using (var path = RoundedRect(t.Thumb, U(6)))
        using (var fill = new SolidBrush(hover ? Color.FromArgb(44, 44, 54) : ThumbBg))
            g.FillPath(fill, path);

        var area = ThumbContentArea(t);
        if (t.Live == IntPtr.Zero)
        {
            var snap = ThumbnailCache.Get(t.Handle);
            if (snap != null)
            {
                var dst = Fit(area, snap.Size);
                if (dragged)
                {
                    using var ia = new System.Drawing.Imaging.ImageAttributes();
                    ia.SetColorMatrix(new System.Drawing.Imaging.ColorMatrix { Matrix33 = 0.3f });
                    g.DrawImage(snap, dst, 0, 0, snap.Width, snap.Height, GraphicsUnit.Pixel, ia);
                }
                else
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    g.DrawImage(snap, dst);
                }
            }
            else
            {
                var big = IconsOf(t.Handle).Big;
                int s = Math.Min(K(48), Math.Min(area.Width, area.Height));
                if (big != null)
                    g.DrawIcon(big, new Rectangle(area.Left + (area.Width - s) / 2, area.Top + (area.Height - s) / 2, s, s));
            }
        }

        // Kenarlık (canlı küçük resim alanın içine çizildiği için kenarlık dışarıda kalır)
        if (hover || selected)
        {
            using var pen = new Pen(selected ? Accent : CardBorderHover, (selected ? 2.5f : 1.5f) * Dpi);
            using var path = RoundedRect(t.Thumb, U(6));
            g.DrawPath(pen, path);
        }

        // Altyazı: simge + başlık (+ üzerine gelince ×)
        var cap = t.Caption;
        int x = cap.Left + U(2);
        var small = IconsOf(t.Handle).Small;
        int iconS = U(16);
        if (small != null)
        {
            g.DrawIcon(small, new Rectangle(x, cap.Top + (cap.Height - iconS) / 2, iconS, iconS));
            x += iconS + U(6);
        }
        int textRight = hover ? t.CloseBtn.Left : cap.Right;
        string title = t.Minimized ? "– " + t.Win.Title : t.Win.Title;
        TextRenderer.DrawText(g, title, _captionFont, Rectangle.FromLTRB(x, cap.Top, textRight, cap.Bottom),
            dragged ? TextFaint : t.Minimized ? TextDim : TextMain,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
            TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

        if (hover)
        {
            var cb = t.CloseBtn;
            cb.Inflate(-U(4), -U(4));
            if (_hoverClose)
                using (var rb = new SolidBrush(CloseRed))
                    g.FillEllipse(rb, cb);
            using var xp = new Pen(_hoverClose ? Color.White : TextDim, 1.6f * Dpi);
            int m = cb.Width / 3;
            g.DrawLine(xp, cb.Left + m, cb.Top + m, cb.Right - m, cb.Bottom - m);
            g.DrawLine(xp, cb.Right - m, cb.Top + m, cb.Left + m, cb.Bottom - m);
        }
    }

    private void PaintAddSlot(Graphics g, AddSlot a)
    {
        bool drop = _dropTarget == a;
        bool hover = _hover == a;
        bool windowDrag = _dragging && _downTarget is Tile;
        Color c = drop ? DropAccent : windowDrag ? Color.FromArgb(70, 130, 95) : hover ? Accent : CardBorder;

        using (var path = RoundedRect(a.Bounds, U(10)))
        {
            if (drop)
                using (var fill = new SolidBrush(Color.FromArgb(38, 52, 44)))
                    g.FillPath(fill, path);
            using var pen = new Pen(c, (drop ? 2.5f : 1.5f) * Dpi) { DashStyle = DashStyle.Dash };
            g.DrawPath(pen, path);
        }

        Color tc = drop ? DropAccent : hover ? Accent : TextDim;
        var plusRect = new Rectangle(a.Bounds.Left, a.Bounds.Top + a.Bounds.Height / 2 - U(40), a.Bounds.Width, U(48));
        TextRenderer.DrawText(g, "+", _plusFont, plusRect, tc,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        var textRect = new Rectangle(a.Bounds.Left + U(6), plusRect.Bottom, a.Bounds.Width - U(12), U(48));
        TextRenderer.DrawText(g, windowDrag ? L.T("ov.dropNew") : L.T("ov.newDesktop"), _smallFont, textRect, tc,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.WordBreak);
    }

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        int d = Math.Max(1, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    // =====================================================================
    // İsabet testi
    // =====================================================================

    private (object? Target, bool OnClose, bool OnHeader) HitTest(Point client)
    {
        if (!Viewport.Contains(client)) return (null, false, false);
        var p = ToContent(client);
        foreach (var sec in _sections)
        {
            foreach (var card in sec.Cards)
            {
                if (!card.Bounds.Contains(p)) continue;
                foreach (var t in card.Tiles)
                    if (t.Bounds.Contains(p))
                        return (t, t.CloseBtn.Contains(p), false);
                return (card, false, card.Header.Contains(p));
            }
            if (sec.Add != null && sec.Add.Bounds.Contains(p)) return (sec.Add, false, false);
            if (sec.Bounds.Contains(p)) return (sec, false, false);
        }
        return (null, false, false);
    }

    // =====================================================================
    // Fare
    // =====================================================================

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        var (target, onClose, onHeader) = HitTest(e.Location);
        _downTarget = target;
        _downOnClose = onClose;
        _downOnHeader = onHeader;
        _downPt = e.Location;
        _downBtn = e.Button;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (_dragging)
        {
            UpdateDrag(e.Location);
            return;
        }

        if (_downTarget != null && _downBtn == MouseButtons.Left && !_downOnClose &&
            (_downTarget is Tile || (_downTarget is Card && _downOnHeader)))
        {
            var d = SystemInformation.DragSize;
            if (Math.Abs(e.X - _downPt.X) > d.Width || Math.Abs(e.Y - _downPt.Y) > d.Height)
            {
                StartDrag(e.Location);
                return;
            }
        }

        var (target, onClose, onHeader) = HitTest(e.Location);
        if (target != _hover || onClose != _hoverClose || onHeader != _hoverHeader)
        {
            if (target != _hover)
            {
                string tip = target switch
                {
                    Tile t => t.Win.Title,
                    AddSlot => L.T("ov.tip.add"),
                    Card when onHeader => L.T("ov.tip.header"),
                    _ => ""
                };
                _tips.SetToolTip(this, tip);
            }
            _hover = target;
            _hoverClose = onClose;
            _hoverHeader = onHeader;
            Cursor = target switch
            {
                Tile => Cursors.Hand,
                Card when onHeader => Cursors.SizeAll,
                Card or AddSlot => Cursors.Hand,
                _ => Cursors.Default
            };
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_dragging) return;
        _hover = null;
        _hoverClose = false;
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_dragging)
        {
            EndDrag();
            _downTarget = null;
            return;
        }

        var down = _downTarget;
        bool downOnClose = _downOnClose;
        _downTarget = null;
        var (target, onClose, _) = HitTest(e.Location);
        if (down == null || target != down || e.Button != _downBtn) return;

        switch (target)
        {
            case Tile t when e.Button == MouseButtons.Middle || (e.Button == MouseButtons.Left && onClose && downOnClose):
                CloseTile(t);
                break;
            case Tile t when e.Button == MouseButtons.Left:
                ActivateTile(t);
                break;
            case Tile t when e.Button == MouseButtons.Right:
                ShowWindowMenu(t, e.Location);
                break;
            case Card c when e.Button == MouseButtons.Left:
                _mgr.SwitchTo(c.Mon.Device, c.Desk.LocalIndex);
                SafeClose();
                break;
            case AddSlot a when e.Button == MouseButtons.Left:
                _mgr.CreateDesktopAndSwitch(a.Section.Mon.Device);
                Reload();
                break;
        }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        ScrollBy(-e.Delta * U(60) / 120);
        if (_dragging) UpdateDrag(PointToClient(Cursor.Position));
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (_dragging && !Capture) CancelDrag();
    }

    // =====================================================================
    // Sürükle-bırak
    // =====================================================================

    private void StartDrag(Point p)
    {
        _dragging = true;
        Capture = true;
        _tips.Hide(this);
        _hover = null;

        if (_downTarget is Tile t)
        {
            var srcSize = t.Live != IntPtr.Zero ? t.Source.Size
                        : ThumbnailCache.Get(t.Handle)?.Size ?? new Size(16, 10);
            var size = Fit(new Rectangle(0, 0, K(220), K(140)), srcSize).Size;
            size = new Size(size.Width + U(6), size.Height + U(6));
            _ghost = new DragGhost(size, Native.IsWindowVisible(t.Handle) ? t.Handle : IntPtr.Zero, t.Source,
                ThumbnailCache.Get(t.Handle), IconsOf(t.Handle).Big, null, _cardFont, Dpi);
        }
        else if (_downTarget is Card c)
        {
            string text = L.F("ov.desktopName", c.Desk.GlobalNumber) + "  ·  " + CountText(c.Desk.Windows.Count);
            var ts = TextRenderer.MeasureText(text, _cardFont);
            _ghost = new DragGhost(new Size(ts.Width + U(36), ts.Height + U(22)), IntPtr.Zero, Rectangle.Empty,
                null, null, text, _cardFont, Dpi);
        }

        UpdateThumbnails(); // sürüklenen pencerenin canlı küçük resmini soluklaştır
        UpdateDrag(p);
        _ghost?.Show();
        Invalidate();
    }

    private void UpdateDrag(Point p)
    {
        if (_ghost != null)
        {
            var sp = PointToScreen(p);
            _ghost.Location = new Point(sp.X - _ghost.Width / 2, sp.Y - _ghost.Height / 2);
        }

        // Kenarlara yaklaşınca otomatik kaydır
        var vp = Viewport;
        if (p.Y < vp.Top + U(40)) ScrollBy(-U(24));
        else if (p.Y > vp.Bottom - U(40)) ScrollBy(U(24));

        object? target = null;
        var (hit, _, _) = HitTest(p);
        if (_downTarget is Tile t)
        {
            target = hit switch
            {
                Tile other => other.Card,
                Card c => c,
                AddSlot a => a,
                _ => null
            };
            if (target == t.Card) target = null; // aynı masaüstü: anlamsız
        }
        else if (_downTarget is Card dc)
        {
            Section? sec = hit switch
            {
                Tile other => other.Card.Section,
                Card c => c.Section,
                AddSlot a => a.Section,
                Section s => s,
                _ => null
            };
            if (sec != null && sec != dc.Section && sec.Mon.Desktops.Count < DesktopManager.MaxDesktopsPerMonitor)
                target = sec;
        }

        if (target != _dropTarget)
        {
            _dropTarget = target;
            if (_ghost != null) _ghost.Valid = target != null;
            Invalidate();
        }
    }

    private void EndDrag()
    {
        var target = _dropTarget;
        var source = _downTarget;
        CancelDrag();

        switch (source, target)
        {
            case (Tile t, Card c):
                _mgr.MoveWindowToDesktop(t.Handle, c.Mon.Device, c.Desk.LocalIndex);
                Reload();
                break;
            case (Tile t, AddSlot a):
                _mgr.MoveWindowToNewDesktop(t.Handle, a.Section.Mon.Device);
                Reload();
                break;
            case (Card c, Section s):
                _mgr.MoveDesktopToMonitor(c.Mon.Device, c.Desk.LocalIndex, s.Mon.Device);
                Reload();
                break;
        }
    }

    private void CancelDrag()
    {
        bool was = _dragging;
        _dragging = false;
        _dropTarget = null;
        _ghost?.Dispose();
        _ghost = null;
        if (Capture) Capture = false;
        if (was && !IsDisposed)
        {
            UpdateThumbnails();
            Invalidate();
        }
    }

    // =====================================================================
    // Eylemler
    // =====================================================================

    private void ActivateTile(Tile t)
    {
        // Önce geçiş yapılır, sonra kapanılır: genel bakış açıkken geçiş animasyonu oynatılmaz
        _mgr.ActivateWindow(t.Handle);
        SafeClose();
    }

    private void CloseTile(Tile t)
    {
        // Pencere kapanınca otomatik tazeleme (AutoRefresh) kutucuğu kaldırır
        DesktopManager.CloseWindow(t.Handle);
    }

    /// <summary>Pencereye sağ tık: herhangi bir masaüstüne / yeni masaüstüne taşı, git, kapat.</summary>
    private void ShowWindowMenu(Tile t, Point at)
    {
        var win = t.Win;
        var menu = new ContextMenuStrip();
        foreach (var mon in _layout)
        {
            foreach (var desk in mon.Desktops)
            {
                string label = L.F("ov.menu.move", mon.Ordinal, desk.GlobalNumber) +
                               (desk.IsCurrent ? L.T("ov.menu.activeSuffix") : "");
                var (device, local) = (mon.Device, desk.LocalIndex);
                var item = menu.Items.Add(label, null, (_, _) =>
                {
                    _mgr.MoveWindowToDesktop(win.Handle, device, local);
                    Reload();
                });
                item.Enabled = desk.LocalIndex != t.Card.Desk.LocalIndex || mon.Device != t.Card.Mon.Device;
            }
            if (mon.Desktops.Count < DesktopManager.MaxDesktopsPerMonitor)
            {
                string device = mon.Device;
                menu.Items.Add(L.F("ov.menu.newDesktop", mon.Ordinal), null, (_, _) =>
                {
                    _mgr.MoveWindowToNewDesktop(win.Handle, device);
                    Reload();
                });
            }
        }
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(L.T("ov.menu.goto"), null, (_, _) => ActivateTile(t));
        menu.Items.Add(L.T("ov.menu.close"), null, (_, _) => CloseTile(t));

        _menu?.Dispose();
        _menu = menu;
        _menuOpen = true;
        menu.Closed += (_, _) => _menuOpen = false;
        menu.Show(this, at);
    }

    // =====================================================================
    // Klavye
    // =====================================================================

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Escape:
                if (_dragging) CancelDrag();
                else if (_search.Text.Length > 0) _search.Clear();
                else SafeClose();
                return true;

            case Keys.Enter:
                var target = _selected ?? _tiles.FirstOrDefault();
                if (target != null) ActivateTile(target);
                return true;

            case Keys.Down:
            case Keys.Tab:
                MoveSelection(+1);
                return true;

            case Keys.Up:
            case Keys.Shift | Keys.Tab:
                MoveSelection(-1);
                return true;

            case Keys.Control | Keys.W:
                if (_selected != null) CloseTile(_selected);
                return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void MoveSelection(int delta)
    {
        if (_tiles.Count == 0) return;
        int i = _selected == null ? (delta > 0 ? -1 : 0) : _tiles.IndexOf(_selected);
        i = (i + delta + _tiles.Count) % _tiles.Count;
        _selected = _tiles[i];
        EnsureVisible(_selected);
        Invalidate();
    }

    // =====================================================================
    // Sürükleme hayaleti
    // =====================================================================

    /// <summary>İmleci izleyen, etkinleşmeyen, tıklamaları geçiren küçük önizleme penceresi.
    /// Canlı pencereler için kendi DWM küçük resmini taşır.</summary>
    private sealed class DragGhost : Form
    {
        private readonly IntPtr _src;
        private readonly Rectangle _srcRect;
        private readonly Bitmap? _image;
        private readonly Icon? _icon;
        private readonly string? _text;
        private readonly Font _font;
        private readonly float _dpi;
        private IntPtr _thumb;
        private bool _valid;

        public DragGhost(Size size, IntPtr liveSource, Rectangle sourceRect, Bitmap? image, Icon? icon,
                         string? text, Font font, float dpi)
        {
            _src = liveSource;
            _srcRect = sourceRect;
            _image = image;
            _icon = icon;
            _text = text;
            _font = font;
            _dpi = dpi;

            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer, true);
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            BackColor = CardBg;
            Size = size;
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x08000000 /* WS_EX_NOACTIVATE */
                            | 0x00000080 /* WS_EX_TOOLWINDOW */
                            | 0x00000020 /* WS_EX_TRANSPARENT */
                            | 0x00000008 /* WS_EX_TOPMOST */;
                return cp;
            }
        }

        public bool Valid
        {
            set
            {
                if (_valid == value) return;
                _valid = value;
                Invalidate();
            }
        }

        private int Border => Math.Max(2, (int)(3 * _dpi));

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (_src == IntPtr.Zero || _srcRect.IsEmpty) return;
            if (Native.DwmRegisterThumbnail(Handle, _src, out _thumb) != 0) { _thumb = IntPtr.Zero; return; }
            var inner = new Rectangle(Border, Border, Width - 2 * Border, Height - 2 * Border);
            var props = new Native.DWM_THUMBNAIL_PROPERTIES
            {
                dwFlags = Native.DWM_TNP_VISIBLE | Native.DWM_TNP_RECTDESTINATION |
                          Native.DWM_TNP_RECTSOURCE | Native.DWM_TNP_OPACITY,
                fVisible = true,
                opacity = 255,
                rcDestination = new Native.RECT(inner),
                rcSource = new Native.RECT(_srcRect)
            };
            Native.DwmUpdateThumbnailProperties(_thumb, ref props);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            using (var pen = new Pen(_valid ? DropAccent : Accent, Border * 2))
                g.DrawRectangle(pen, 0, 0, Width, Height);

            var inner = new Rectangle(Border, Border, Width - 2 * Border, Height - 2 * Border);
            if (_text != null)
            {
                TextRenderer.DrawText(g, _text, _font, inner, Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            }
            else if (_thumb == IntPtr.Zero)
            {
                if (_image != null)
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    g.DrawImage(_image, inner);
                }
                else if (_icon != null)
                {
                    int s = Math.Min(inner.Width, Math.Min(inner.Height, (int)(48 * _dpi)));
                    g.DrawIcon(_icon, new Rectangle(inner.Left + (inner.Width - s) / 2, inner.Top + (inner.Height - s) / 2, s, s));
                }
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (_thumb != IntPtr.Zero)
            {
                Native.DwmUnregisterThumbnail(_thumb);
                _thumb = IntPtr.Zero;
            }
            base.Dispose(disposing);
        }
    }
}
