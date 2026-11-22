using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace BatteryTray;

/// <summary>
/// 自绘右键菜单。
///
/// 不用 WinForms 的 ContextMenuStrip，原因是它有三个无法根治的毛病：
///   ① 是没有 owner 的顶层窗口，会跑到任务栏上（表现为"多出一个窗口"）；
///   ② 把它设了 owner 之后，"关闭开机自启"会连带让贴片一起消失；
///   ③ 外观是 Win32 经典菜单，无法做成圆角/跟随主题的现代样式。
///
/// 这里用和贴片同一套技术：WS_EX_LAYERED + WS_EX_TOOLWINDOW + UpdateLayeredWindow，
/// 自己画圆角卡片；点击菜单外部（或按 Esc）立即关闭。
/// </summary>
public sealed class PillMenu : IDisposable
{
    public sealed record Entry(string Text, string? Hint, bool Enabled, bool Checked, bool IsSeparator, Action? Action);

    const string ClassName = "LegionBatteryPillMenuWnd";
    static readonly Dictionary<IntPtr, PillMenu> Instances = new();
    static ushort _classAtom;

    const int WS_POPUP = unchecked((int)0x80000000);
    const int WS_EX_LAYERED = 0x00080000;
    const int WS_EX_TOOLWINDOW = 0x00000080;
    const int WS_EX_TOPMOST = 0x00000008;
    const int WS_EX_NOACTIVATE = 0x08000000;

    const int WM_MOUSEACTIVATE = 0x0021;
    const int MA_NOACTIVATE = 3;
    const int WM_MOUSEMOVE = 0x0200;
    const int WM_LBUTTONDOWN = 0x0201;
    const int WM_LBUTTONUP = 0x0202;
    const int WM_MOUSELEAVE = 0x02A3;
    const int WM_KEYDOWN = 0x0100;
    const int WM_ACTIVATE = 0x0006;
    const int WM_KILLFOCUS = 0x0008;
    const int WM_CAPTURECHANGED = 0x0215;
    const int WM_DESTROY = 0x0002;

    const uint SWP_NOSIZE = 0x0001, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;
    const int ULW_ALPHA = 2;

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential)]
    struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public int ptX, ptY; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct WNDCLASSEX
    {
        public int cbSize;                 // 与 TaskbarPill 保持一致：用 int 而非 uint
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    delegate IntPtr WndProcDelegate(IntPtr h, uint m, IntPtr w, IntPtr l);

    [DllImport("user32.dll", SetLastError = true)] static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateWindowEx(int ex, string cls, string title, int style, int x, int y, int w, int h,
        IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll", SetLastError = true)] static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern IntPtr DefWindowProc(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint f);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr SetCapture(IntPtr h);
    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern IntPtr GetCapture();
    [DllImport("user32.dll")] static extern bool PeekMessage(out MSG m, IntPtr h, uint a, uint b, uint r);
    [DllImport("user32.dll")] static extern int GetMessage(out MSG m, IntPtr h, uint a, uint b);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG m);
    [DllImport("user32.dll")] static extern IntPtr DispatchMessage(ref MSG m);
    [DllImport("user32.dll")] static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool InvalidateRect(IntPtr h, IntPtr r, bool erase);
    [DllImport("user32.dll")] static extern bool UpdateWindow(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, IntPtr pptDst, ref SIZE psize,
        IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int flags);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")]
    static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER hdr, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string? n);

    static readonly WndProcDelegate ProcDel = StaticProc;

    IntPtr _hwnd;
    List<Entry> _entries = new();
    int _hover = -1;
    int _width, _height;
    Rectangle[] _rows = Array.Empty<Rectangle>();
    readonly double _dpi;   // 真实缩放倍数（150% = 1.5）。

    /// <summary>按 DPI 换算像素，四舍五入。用 double 保存缩放倍数，
    /// 避免把 1.5 舍入成 2 导致字号和内边距整体翻倍（实测菜单因此偏大）。</summary>
    int Px(double v) => Math.Max(1, (int)Math.Round(v * _dpi));

    // 主题
    bool _light;
    Color _bg, _bgBorder, _ink, _inkDim, _hoverBg, _accent, _sep;

    public bool IsOpen { get; private set; }

    public PillMenu()
    {
        using var probe = Graphics.FromHwnd(IntPtr.Zero);
        _dpi = Math.Max(1.0, probe.DpiX / 96.0);
    }

    /// <summary>在屏幕坐标 (x,y) 处弹出。items 里的 Action 会在关闭后执行。</summary>
    public void Show(int x, int y, IReadOnlyList<Entry> items)
    {
        Close();
        _closePosted = false;
        _hover = -1;
        _logged = 0;

        _entries = items.ToList();
        _light = Theme.IsLightTaskbar();
        ApplyTheme();
        Measure();

        EnsureClass();

        // 先算好最终位置，再一次性用正确坐标/尺寸建窗口。
        // 不在之后调 SetWindowPos —— 实测那样会让 UpdateLayeredWindow 返回 FALSE
        // （窗口被移动/改尺寸后，DWM 的表面状态和 psize 不再匹配）。
        //
        // 用光标所在的显示器，而不是固定主屏：任务栏可能在副屏上，
        // 用主屏边界去钳制会把菜单拽到另一块屏幕去。
        var screen = Screen.FromPoint(new Point(x, y)).WorkingArea;
        int px = Math.Min(x, screen.Right - _width - 4);
        int py = y - _height;

        // 上方放不下就翻到下方（锚点 y 是贴片上沿，再减去菜单高度就是贴片上方）
        if (py < screen.Top + 4) py = y + 4;
        if (py + _height > screen.Bottom - 4) py = Math.Max(screen.Top + 4, screen.Bottom - _height - 4);

        px = Math.Max(screen.Left + 4, px);
        _origin = new Point(px, py);

        // 记下创建窗口的线程，Close() 据此判断能不能直接 DestroyWindow
        _uiThreadId = Environment.CurrentManagedThreadId;

        // 记下同步上下文：菜单项的动作要回到 UI 线程执行（动作里会碰贴片窗口）
        _uiCtx = SynchronizationContext.Current;

        _hwnd = CreateWindowEx(
            WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_NOACTIVATE,
            ClassName, "", WS_POPUP, px, py, _width, _height,
            IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
        {
            TrayContext.Log($"menu: CreateWindowEx 失败 err={Marshal.GetLastWin32Error()}");
            return;
        }

        lock (Instances) Instances[_hwnd] = this;

        // 先提交位图再显示（顺序反过来实测会让 DWM 拒绝分层内容）
        Render();
        ShowWindow(_hwnd, 4 /*SW_SHOWNOACTIVATE*/);
        IsOpen = true;
        TrayContext.Log($"menu: 打开 ({px},{py}) {_width}x{_height}");
    }

    Point _origin;

    /// <summary>创建菜单窗口的那个线程（UI 线程）。DestroyWindow 只能在它上面调。</summary>
    int _uiThreadId;

    /// <summary>UI 线程的同步上下文，菜单项动作靠它回到 UI 线程执行。</summary>
    SynchronizationContext? _uiCtx;

    /// <summary>已经投递过关闭请求，避免 watcher 线程每次循环都重复投递。</summary>
    bool _closePosted;

    const int WM_APP_CLOSE_SELF = 0x8000 + 1;

    /// <summary>
    /// 关闭菜单。
    ///
    /// **注意：DestroyWindow 不能跨线程调用** —— 从后台线程调它会静默失败，
    /// 但 _hwnd 已经被清 0，于是窗口变成"孤儿"永远留在屏幕上
    /// （实测症状：自动关闭日志打了，菜单却还在）。
    /// 所以这里判断线程：不在 UI 线程就投递一条消息，让 UI 线程自己销毁。
    /// </summary>
    public void Close()
    {
        IntPtr h = _hwnd;
        if (h == IntPtr.Zero)
        {
            IsOpen = false;
            _hover = -1;
            return;
        }

        if (Environment.CurrentManagedThreadId != _uiThreadId)
        {
            // 后台线程：交给 UI 线程去销毁（只投一次，别每个 tick 都投）
            if (!_closePosted)
            {
                _closePosted = true;
                PostMessage(h, WM_APP_CLOSE_SELF, IntPtr.Zero, IntPtr.Zero);
            }
            return;
        }

        // 无论从哪条路径关闭，都要把自动关闭的全局钩子摘掉。
        // 原来是只在下次 Show / Dispose 时才摘 —— 菜单关掉后钩子仍然挂在全局鼠标消息上。
        StopAutoClose();

        _hwnd = IntPtr.Zero;
        lock (Instances) Instances.Remove(h);
        DestroyWindow(h);
        IsOpen = false;
        _hover = -1;
        _closePosted = false;
    }

    void EnsureClass()
    {
        if (_classAtom != 0) return;
        var wc = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(ProcDel),
            hInstance = GetModuleHandle(null),
            lpszClassName = ClassName,
        };
        _classAtom = RegisterClassEx(ref wc);
    }

    void ApplyTheme()
    {
        _accent = Theme.Accent(_light, -1, false, true);
        if (_light)
        {
            _bg = Color.FromArgb(0xFB, 0xFB, 0xFC);
            _bgBorder = Color.FromArgb(0xE2, 0xE4, 0xE8);
            _ink = Color.FromArgb(0x1B, 0x1B, 0x1B);
            _inkDim = Color.FromArgb(0x6B, 0x72, 0x80);
            _hoverBg = Color.FromArgb(0xEE, 0xF2, 0xF8);
            _sep = Color.FromArgb(0xE8, 0xEA, 0xEE);
        }
        else
        {
            _bg = Color.FromArgb(0x2C, 0x2C, 0x30);
            _bgBorder = Color.FromArgb(0x3F, 0x3F, 0x46);
            _ink = Color.FromArgb(0xF3, 0xF3, 0xF3);
            _inkDim = Color.FromArgb(0x9C, 0xA3, 0xAF);
            _hoverBg = Color.FromArgb(0x3A, 0x3A, 0x42);
            _sep = Color.FromArgb(0x3A, 0x3A, 0x40);
        }
    }

    // ——— 布局 ———
    static readonly int[] PadTop = { 6, 6 }, PadBottom = { 6, 6 };

    void Measure()
    {
        int fz = Math.Max(15, (int)Math.Round(12.5 * _dpi));   // 正文字号（像素）
        using var font = new Font("Segoe UI", fz, FontStyle.Regular, GraphicsUnit.Pixel);
        using var probe = Graphics.FromHwnd(IntPtr.Zero);

        int rowH = font.Height + Px(6);
        int sepH = Px(6);
        int padX = Px(9);
        int iconW = Px(13);
        int padTop = Px(5), padBottom = Px(5);

        int textMax = 0;
        foreach (var e in _entries)
        {
            if (e.IsSeparator) continue;
            var sz = probe.MeasureString(e.Text, font);
            int w = (int)Math.Ceiling(sz.Width);
            if (e.Hint != null)
            {
                var hs = probe.MeasureString(e.Hint, font);
                w += (int)Math.Ceiling(hs.Width) + Px(6);
            }
            textMax = Math.Max(textMax, w);
        }

        _width = Math.Max(Px(70), padX * 2 + iconW + textMax + Px(3));

        int y = padTop;
        _rows = new Rectangle[_entries.Count];
        for (int i = 0; i < _entries.Count; i++)
        {
            int h = _entries[i].IsSeparator ? sepH : rowH;
            _rows[i] = new Rectangle(0, y, _width, h);
            y += h;
        }
        _height = y + padBottom;
    }

    // ——— 绘制 ———
    void Render()
    {
        if (_hwnd == IntPtr.Zero) return;

        int radius = Px(9);
        using var bmp = new Bitmap(_width, _height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.Clear(Color.Transparent);

            using (var path = Rounded(new Rectangle(0, 0, _width - 1, _height - 1), radius))
            {
                using var bg = new SolidBrush(_bg);
                g.FillPath(bg, path);
                using var pen = new Pen(_bgBorder, 1f);
                g.DrawPath(pen, path);
            }

            int fz = Math.Max(15, (int)Math.Round(12.5 * _dpi));
            using var font = new Font("Segoe UI", fz, FontStyle.Regular, GraphicsUnit.Pixel);
            using var ink = new SolidBrush(_ink);
            using var dim = new SolidBrush(_inkDim);
            using var sf = new StringFormat(StringFormat.GenericTypographic)
            {
                LineAlignment = StringAlignment.Center,
                FormatFlags = StringFormatFlags.NoWrap,
            };

            int padX = Px(9);
            int iconW = Px(13);

            for (int i = 0; i < _entries.Count; i++)
            {
                var e = _entries[i];
                var r = _rows[i];

                if (e.IsSeparator)
                {
                    using var pen = new Pen(_sep, 1f);
                    int ly = r.Top + r.Height / 2;
                    g.DrawLine(pen, padX, ly, _width - padX, ly);
                    continue;
                }

                if (i == _hover && e.Enabled)
                {
                    using var hp = Rounded(new Rectangle(padX / 2, r.Top + 1, _width - padX, r.Height - 2), Px(4));
                    using var hb = new SolidBrush(_hoverBg);
                    g.FillPath(hb, hp);
                }

                var textBrush = e.Enabled ? ink : dim;

                // 左侧：勾选对勾
                if (e.Checked)
                {
                    float cx = padX / 2f + Px(3);
                    float cy = r.Top + r.Height / 2f;
                    using var pen = new Pen(_accent, Math.Max(1.2f, 1.4f * (float)_dpi)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                    g.DrawLines(pen, new[]
                    {
                        new PointF(cx, cy),
                        new PointF(cx + 2.4f * (float)_dpi, cy + 2.7f * (float)_dpi),
                        new PointF(cx + 6.8f * (float)_dpi, cy - 2.7f * (float)_dpi),
                    });
                }

                float tx = padX / 2f + iconW;
                var textRect = new RectangleF(tx, r.Top, _width - tx - padX, r.Height);
                g.DrawString(e.Text, font, textBrush, textRect, sf);

                if (e.Hint != null)
                {
                    var hs = g.MeasureString(e.Hint, font);
                    float hx = _width - padX - hs.Width;
                    g.DrawString(e.Hint, font, dim, new RectangleF(hx, r.Top, hs.Width + 2, r.Height), sf);
                }
            }
        }

        bool ok = TaskbarPill.SubmitLayered(_hwnd, bmp);
        if (_logged++ < 4)
        {
            GetWindowRect(_hwnd, out RECT wr);
            TrayContext.Log($"menu: render ulw={ok} 目标=({_origin.X},{_origin.Y}) {_width}x{_height} " +
                            $"实际=({wr.Left},{wr.Top}) {wr.Right - wr.Left}x{wr.Bottom - wr.Top}");
        }
    }

    int _logged;

    static GraphicsPath Rounded(Rectangle r, int radius)
    {
        var p = new GraphicsPath();
        int d = Math.Max(2, radius * 2);
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }


    // ——— 窗口过程 ———
    static IntPtr StaticProc(IntPtr h, uint m, IntPtr w, IntPtr l)
    {
        PillMenu? self;
        lock (Instances) Instances.TryGetValue(h, out self);

        if (self == null) return DefWindowProc(h, m, w, l);

        switch (m)
        {
            // 后台线程请求关闭（DestroyWindow 必须回到 UI 线程执行）
            case WM_APP_CLOSE_SELF:
                self.Close();
                return IntPtr.Zero;

            // 关键：本窗口带 WS_EX_NOACTIVATE，默认的 MA_ACTIVATE 会把第一次点击
            // 当成"激活点击"吞掉（点了没反应）。返回 MA_NOACTIVATE 让点击直接投递过来。
            case WM_MOUSEACTIVATE:
                return new IntPtr(MA_NOACTIVATE);

            case WM_MOUSEMOVE:
            {
                int mx = (short)(l.ToInt64() & 0xFFFF);
                int my = (short)((l.ToInt64() >> 16) & 0xFFFF);
                int hit = self.HitTest(mx, my);
                if (hit != self._hover)
                {
                    self._hover = hit;
                    self.Render();
                }
                return IntPtr.Zero;
            }
            case WM_LBUTTONUP:
            {
                int mx = (short)(l.ToInt64() & 0xFFFF);
                int my = (short)((l.ToInt64() >> 16) & 0xFFFF);
                int hit = self.HitTest(mx, my);
                if (hit >= 0 && self._entries[hit].Enabled && self._entries[hit].Action is { } act)
                {
                    self.Close();
                    // 菜单关掉之后再执行动作。动作里会碰贴片窗口（刷新/退出/改自启），
                    // 必须回到 UI 线程执行 —— 丢到线程池里跨线程 DestroyWindow 会静默失败。
                    var ctx = self._uiCtx;
                    if (ctx != null)
                    {
                        ctx.Post(_ =>
                        {
                            try { act(); } catch (Exception ex) { TrayContext.Log("menu: 动作异常 " + ex.Message); }
                        }, null);
                    }
                    else
                    {
                        try { act(); } catch (Exception ex) { TrayContext.Log("menu: 动作异常 " + ex.Message); }
                    }
                    return IntPtr.Zero;
                }
                self.Close();
                return IntPtr.Zero;
            }
            // 注意：**不要**在 WM_KILLFOCUS / WM_ACTIVATE / WM_CAPTURECHANGED 里关闭菜单。
            // 这个窗口带 WS_EX_NOACTIVATE，本来就拿不到稳定焦点，
            // 那些消息会在菜单刚显示时就到达，导致它立刻消失。
            // "点击菜单外部关闭"由 StartAutoClose 轮询负责。

            // 菜单带 WS_EX_NOACTIVATE，拿不到键盘焦点，WM_KEYDOWN 实际上不会到达（保留兜底）。
            // 真正的 Esc 由 StartAutoClose 的观察线程用 GetAsyncKeyState 轮询处理。
            case WM_KEYDOWN when w.ToInt64() == 0x1B:   // Esc
                self.Close();
                return IntPtr.Zero;

            case WM_DESTROY:
                lock (Instances) Instances.Remove(h);
                return IntPtr.Zero;
        }
        return DefWindowProc(h, m, w, l);
    }

    int HitTest(int x, int y)
    {
        for (int i = 0; i < _rows.Length; i++)
            if (_rows[i].Contains(x, y)) return i;
        return -1;
    }

    /// <summary>
    /// 让菜单在"失去焦点"时自动关闭。
    ///
    /// 之前的实现是每 60ms 轮询 GetAsyncKeyState —— 快速点击（按下+抬起 < 60ms）会整个漏掉，
    /// 表现就是"点了别处菜单不关"。现在改用低层鼠标钩子，按下即判定，不会漏。
    ///
    /// 判定规则（都很直接）：
    ///   · 在菜单外按下左键/右键 → 关
    ///   · 在菜单外移动且按着键      → 关
    ///   · 前台窗口变成别的程序      → 关
    ///   · 按 Esc                    → 关
    /// </summary>
    public void StartAutoClose(Func<IntPtr> ownerHwnd)
    {
        var hwnd = _hwnd;
        if (hwnd == IntPtr.Zero) return;

        StopAutoClose();
        _autoCloseHwnd = hwnd;
        _autoCloseProc = (nCode, wParam, lParam) =>
        {
            if (nCode >= 0 && _hwnd == hwnd)
            {
                int msg = wParam.ToInt32();
                if (msg is WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN or WM_MOUSEMOVE)
                {
                    var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                    bool inside = info.ptX >= _origin.X && info.ptX < _origin.X + _width &&
                                  info.ptY >= _origin.Y && info.ptY < _origin.Y + _height;

                    if (!inside)
                    {
                        bool buttonDown =
                            (GetAsyncKeyState(0x01) & 0x8000) != 0 ||   // 左键
                            (GetAsyncKeyState(0x02) & 0x8000) != 0 ||   // 右键
                            (GetAsyncKeyState(0x04) & 0x8000) != 0;     // 中键

                        // 按下即关；MOUSEMOVE 只在真的有键按下时才关（避免只是划过就关闭）
                        if (msg == WM_MOUSEMOVE ? buttonDown : true)
                        {
                            Close();
                        }
                    }
                }
            }
            return CallNextHookEx(_autoCloseHook, nCode, wParam, lParam);
        };

        _autoCloseHook = SetWindowsHookEx(WH_MOUSE_LL, _autoCloseProc, GetModuleHandle(null), 0);
        if (_autoCloseHook == IntPtr.Zero || _autoCloseHook == new IntPtr(-1))
        {
            TrayContext.Log($"menu: 自动关闭钩子安装失败 err={Marshal.GetLastWin32Error()}（改用轮询）");
            _autoCloseHook = IntPtr.Zero;
        }

        // 兜底轮询（不依赖钩子，也不依赖注入输入是否可见）。
        //
        // 注意：菜单是 WS_EX_NOACTIVATE，它**永远不会成为前台窗口**，
        // 所以不能用 "fg != 菜单" 判断失去焦点（那样菜单一弹出就立刻关）。
        // 记住弹出时的前台窗口，只有它变了才算用户切走。
        IntPtr fgAtShow = GetForegroundWindow();
        var w = new Thread(() =>
        {
            var outsideSince = DateTime.MinValue;
            bool everInside = false;   // 光标是否曾经进入过菜单

            while (_hwnd == hwnd)
            {
                Thread.Sleep(30);
                if (_hwnd != hwnd) break;

                // ⓿ Esc：菜单没有键盘焦点，收不到 WM_KEYDOWN，只能直接问系统按键状态。
                //    GetAsyncKeyState 是全局的 —— 用户在**别的程序**里按 Esc 也会关菜单。
                //    这是有意的：菜单是无焦点的覆盖层，"按 Esc"在这里视同"点了别处"。
                if ((GetAsyncKeyState(0x1B) & 0x8000) != 0)
                {
                    TrayContext.Log("menu: 按下 Esc，自动关闭");
                    Close();
                    break;
                }

                // ① 前台窗口从"弹出时那个"变成别的 → 用户切到别的程序了
                IntPtr fg = GetForegroundWindow();
                if (fg != IntPtr.Zero && fgAtShow != IntPtr.Zero && fg != fgAtShow)
                {
                    TrayContext.Log("menu: 前台窗口变化，自动关闭");
                    Close();
                    break;
                }

                bool inside = false;
                if (GetCursorPos(out POINT p))
                    inside = p.X >= _origin.X && p.X < _origin.X + _width &&
                             p.Y >= _origin.Y && p.Y < _origin.Y + _height;

                // ② 鼠标在菜单外被按下 → 点了别处，立刻关
                bool buttonDown = (GetAsyncKeyState(0x01) & 0x8000) != 0 ||
                                  (GetAsyncKeyState(0x02) & 0x8000) != 0;
                if (!inside && buttonDown)
                {
                    TrayContext.Log("menu: 菜单外按下鼠标，自动关闭");
                    Close();
                    break;
                }

                // ③ 光标进过菜单之后再离开，超过 800ms 就关（"移开即失焦"）
                //
                //    必须有 everInside 这一层：菜单是在贴片**上方**展开的，
                //    刚弹出时鼠标还停在贴片位置（位于菜单外），
                //    若直接计时会在用户还没移进菜单时就把菜单关掉。
                if (inside)
                {
                    everInside = true;
                    outsideSince = DateTime.MinValue;
                }
                else if (everInside)
                {
                    if (outsideSince == DateTime.MinValue) outsideSince = DateTime.UtcNow;
                    else if ((DateTime.UtcNow - outsideSince).TotalMilliseconds > 800)
                    {
                        TrayContext.Log("menu: 光标离开菜单超时，自动关闭");
                        Close();
                        break;
                    }
                }
            }
        })
        { IsBackground = true, Name = "pillmenu-watch" };
        w.Start();
    }

    IntPtr _autoCloseHook = IntPtr.Zero;
    IntPtr _autoCloseHwnd = IntPtr.Zero;
    LowLevelMouseProc? _autoCloseProc;

    void StopAutoClose()
    {
        if (_autoCloseHook != IntPtr.Zero)
        {
            try { UnhookWindowsHookEx(_autoCloseHook); } catch { }
            _autoCloseHook = IntPtr.Zero;
        }
        _autoCloseHwnd = IntPtr.Zero;
        // 注意：不把 _autoCloseProc 置 null。
        // Close() 可能就是从钩子回调内部调用的（点菜单外触发），
        // 那时置 null 等于在回调执行期间丢掉它唯一的引用。
    }

    delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    const int WH_MOUSE_LL = 14;
    const int WM_RBUTTONDOWN = 0x0204;
    const int WM_MBUTTONDOWN = 0x0207;

    [StructLayout(LayoutKind.Sequential)]
    struct MSLLHOOKSTRUCT
    {
        public int ptX, ptY;
        public uint mouseData, flags, time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc proc, IntPtr hMod, uint threadId);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern short GetAsyncKeyState(int vKey);

    public void Dispose()
    {
        StopAutoClose();
        Close();
    }
}

/// <summary>主题判断与配色，菜单和贴片共用。</summary>
public static class Theme
{
    public static bool IsLightTaskbar()
    {
        try
        {
            object? v = Microsoft.Win32.Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "SystemUsesLightTheme", 1);
            return v is int i ? i != 0 : true;
        }
        catch { return true; }
    }

    public static Color Accent(bool light, int percent, bool charging, bool connected)
    {
        if (!connected) return light ? Color.FromArgb(0x6B, 0x72, 0x80) : Color.FromArgb(0x9C, 0xA3, 0xAF);
        if (charging) return light ? Color.FromArgb(0x15, 0x65, 0xC0) : Color.FromArgb(0x60, 0xA5, 0xFA);
        if (percent >= 0 && percent < 25) return light ? Color.FromArgb(0xC0, 0x39, 0x2B) : Color.FromArgb(0xF8, 0x71, 0x71);
        if (percent >= 0 && percent < 60) return light ? Color.FromArgb(0xB2, 0x6A, 0x00) : Color.FromArgb(0xFB, 0xBF, 0x24);
        return light ? Color.FromArgb(0x1B, 0x7F, 0x3B) : Color.FromArgb(0x4A, 0xDE, 0x80);
    }
}









