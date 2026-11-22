using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace BatteryTray;

/// <summary>
/// 任务栏电量贴片（原生分层窗口 + 32 位预乘 ARGB 位图）。
///
/// 两种挂载方式，优先「真嵌入」：
///  1. Embed：SetParent 到 Shell_TrayWnd，成为任务栏的子窗口 ——
///     Z 序在任务栏内部（任务栏/开始菜单都盖不住），不进 Alt+Tab，跟随任务栏移动与自动隐藏；
///  2. Overlay：跨进程 SetParent 被系统拒绝时（受限会话返回 err=5）退化成置顶小窗口 ——
///     仍带 WS_EX_TOOLWINDOW + WS_EX_NOACTIVATE（所以也不进 Alt+Tab），并每 500 ms 重新置顶防被任务栏压住。
///
/// 内容不走 WM_PAINT：Win11 任务栏是 XAML 合成的，普通子窗口会被合成层盖住；
/// 分层窗口有独立 DWM 合成面，只有 UpdateLayeredWindow 提交的位图才能显示在 XAML 之上。
/// </summary>
internal sealed class TaskbarPill : IDisposable
{
    const int WS_POPUP = unchecked((int)0x80000000);
    const int WS_CHILD = 0x40000000;
    const int WS_CLIPSIBLINGS = 0x04000000;
    const int WS_EX_TOPMOST = 0x00000008;
    const int WS_EX_LAYERED = 0x00080000;
    const int WS_EX_TOOLWINDOW = 0x00000080;
    const int WS_EX_NOACTIVATE = 0x08000000;

    const int WM_DESTROY = 0x0002;
    const int WM_PAINT = 0x000F;
    const int WM_ERASEBKGND = 0x0014;
    const int WM_MOUSEACTIVATE = 0x0021;
    const int WM_SETCURSOR = 0x0020;
    const int WM_NCHITTEST = 0x0084;
    const int HTCLIENT = 1;
    const int WM_LBUTTONUP = 0x0202;
    const int WM_RBUTTONUP = 0x0205;
    const int WM_CONTEXTMENU = 0x007B;

    // 低层钩子命中后不直接执行业务，而是往自己的窗口投一条消息，
    // 让所有点击都从 UI 线程的 WndProc 单点分发（见 DispatchClick）。
    const int WM_APP_PILL_LEFT = 0x8000 + 11;
    const int WM_APP_PILL_RIGHT = 0x8000 + 12;

    const int MA_ACTIVATE = 1;
    const int MA_NOACTIVATE = 3;
    const int IDC_HAND = 32649;
    const int GWL_STYLE = -16;
    const int GWL_EXSTYLE = -20;
    const int GWLP_HWNDPARENT = -8;
    const int GW_OWNER = 4;
    const uint GW_HWNDNEXT = 2;
    const int SW_SHOWNOACTIVATE = 4;
    const int SW_HIDE = 0;

    // 只在「被别的窗口压住」时才重新置顶，避免每 500 ms 硬抬一次导致点击任务栏时闪一下
    const uint EVENT_SYSTEM_FOREGROUND = 3;
    const uint EVENT_OBJECT_REORDER = 0x8004;
    const uint WINEVENT_OUTOFCONTEXT = 0;
    const uint WINEVENT_SKIPOWNPROCESS = 2;

    const byte AC_SRC_OVER = 0x00;
    const byte AC_SRC_ALPHA = 0x01;
    const int ULW_ALPHA = 0x02;

    const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010, SWP_SHOWWINDOW = 0x0040;
    static readonly IntPtr HWND_TOPMOST = new(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WNDCLASSEX
    {
        public int cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    struct SIZE { public int Cx, Cy; }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct BLENDFUNCTION
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public short biPlanes;
        public short biBitCount;
        public int biCompression;
        public int biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public int biClrUsed;
        public int biClrImportant;
    }

    delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern ushort RegisterClassEx(ref WNDCLASSEX c);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateWindowEx(int exStyle, string cls, string title, int style,
        int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetParent(IntPtr child, IntPtr parent);

    [DllImport("user32.dll", SetLastError = true)]
    static extern int MapWindowPoints(IntPtr from, IntPtr to, ref POINT pts, uint count);

    [DllImport("user32.dll")]
    static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    static extern IntPtr GetThreadDpiAwarenessContext();

    [DllImport("user32.dll")]
    static extern int GetAwarenessFromDpiAwarenessContext(IntPtr ctx);

    [DllImport("kernel32.dll")]
    static extern int GetLastError();
    [DllImport("user32.dll")] static extern IntPtr GetParent(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool MoveWindow(IntPtr hwnd, int x, int y, int w, int h, bool repaint);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr hwnd, out RECT r);
    [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr hwnd, ref POINT p);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("user32.dll")] static extern IntPtr SetCursor(IntPtr cursor);
    [DllImport("user32.dll")] static extern IntPtr LoadCursor(IntPtr instance, IntPtr name);
    [DllImport("user32.dll")] static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string? title);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
    [DllImport("user32.dll")] static extern IntPtr GetTopWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT p);

    delegate void WinEventDelegate(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventDelegate proc,
        uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")]
    static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER hdr, uint usage,
        out IntPtr bits, IntPtr section, uint offset);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, IntPtr pptDst, ref SIZE psize,
        IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION blend, int flags);

    [StructLayout(LayoutKind.Sequential)]
    struct PAINTSTRUCT
    {
        public IntPtr hdc;
        public bool fErase;
        public RECT rcPaint;
        public bool fRestore;
        public bool fIncUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] rgbReserved;
    }

    [DllImport("user32.dll")] static extern IntPtr BeginPaint(IntPtr hwnd, out PAINTSTRUCT ps);
    [DllImport("user32.dll")] static extern bool EndPaint(IntPtr hwnd, ref PAINTSTRUCT ps);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr GetModuleHandle(string? name);

    static WndProcDelegate? _proc;                 // 必须静态保活，否则 GC 回收后崩溃
    static WinEventDelegate? _winEventProc;
    static IntPtr _winEventHook;
    static ushort _classAtom;
    static readonly object ClassGate = new();
    static readonly Dictionary<IntPtr, TaskbarPill> Instances = new();
    const string ClassName = "LegionBatteryPillWnd";

    readonly System.Windows.Forms.Timer _tick;
    IntPtr _hwnd;
    bool _embedded;
    IntPtr _embedParent;      // 实际父窗口（可能不是 Shell_TrayWnd 本身）
    int _coordOffsetY;        // 非 0 表示 SetWindowPos 按父窗口客户区解释，需要减去这个偏移
    bool _owned;              // 置顶模式下把自己的 owner 设成任务栏，点击任务栏就不会把它盖住
    int _raises;
    bool _layeredOk = true;

    int _percent = -1;
    bool _charging;
    bool _connected;
    int _logged;
    (int x, int y, int w, int h) _lastLayout = (-1, -1, -1, -1);

    /// <summary>同类点击的去重时间戳：钩子和窗口消息可能对同一次点击各报一遍。</summary>
    long _lastLeftTick, _lastRightTick;

    /// <summary>创建失败后的冷却时间，避免每 500ms 重试一次并刷日志。</summary>
    long _lastCreateFailTick;

    public event Action? LeftClick;
    public event Action? RightClick;

    /// <summary>"embed" / "overlay" / "none"，写日志用。</summary>
    public string ModeName { get; private set; } = "none";
    public bool Alive => _hwnd != IntPtr.Zero && IsWindow(_hwnd);

    /// <summary>贴片窗口句柄。菜单之类的临时窗口拿它当 owner，就不会出现在任务栏上。</summary>
    public IntPtr WindowHandle => _hwnd;

    /// <summary>
    /// 右键菜单的锚点（屏幕坐标）。取贴片左上角再往上抬一点，
    /// 菜单会从贴片正上方展开，贴着任务栏而不遮住它。
    /// </summary>
    public void GetMenuAnchor(out int x, out int y)
    {
        if (_hwnd != IntPtr.Zero && GetWindowRect(_hwnd, out RECT r))
        {
            x = r.Left;
            y = r.Top - 4;
            return;
        }
        var cursor = Cursor.Position;
        x = cursor.X;
        y = cursor.Y;
    }

    public TaskbarPill()
    {
        Create();

        _tick = new System.Windows.Forms.Timer { Interval = 500 };
        _tick.Tick += (_, _) => Tick();
        _tick.Start();
    }

    // ——— 创建 / 挂载 ———

    static void EnsureClass()
    {
        lock (ClassGate)
        {
            if (_classAtom != 0) return;
            _proc = WndProcStatic;
            var wc = new WNDCLASSEX
            {
                cbSize = Marshal.SizeOf<WNDCLASSEX>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
                hInstance = GetModuleHandle(null),
                hbrBackground = IntPtr.Zero,
                lpszClassName = ClassName,
            };
            _classAtom = RegisterClassEx(ref wc);

            if (_classAtom != 0 && _winEventHook == IntPtr.Zero)
            {
                // 前台变化 + Z 序变化：用来在"被压住"的瞬间把贴片抬回来
                _winEventProc = OnForegroundChange;
                _winEventHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_OBJECT_REORDER,
                    IntPtr.Zero, _winEventProc, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
            }
        }
    }

    void Create()
    {
        EnsureClass();
        if (_classAtom == 0)
        {
            TrayContext.Log($"pill: RegisterClassEx failed err={Marshal.GetLastWin32Error()}");
            return;
        }

        IntPtr tray = FindWindow("Shell_TrayWnd", null);

        // 必须先以顶层窗口创建：跨进程 CreateWindowEx 直接指定 parent 会被拒绝（err=5）
        // WS_EX_NOACTIVATE 必须带上：否则点贴片会把焦点从当前程序抢走
        // （表现为输入框失焦、正在打的字断掉）。
        int ex = WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_NOACTIVATE;
        _hwnd = CreateWindowEx(ex, ClassName, "", WS_POPUP,
            0, 0, 10, 10, IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
        {
            TrayContext.Log($"pill: CreateWindowEx failed err={Marshal.GetLastWin32Error()}");
            return;
        }

        lock (Instances) Instances[_hwnd] = this;

        _embedded = false;
        if (tray != IntPtr.Zero)
        {
            SetParent(_hwnd, tray);
            int setParentErr = Marshal.GetLastWin32Error();

            // 关键：不能用 GetParent 判断。跨进程 SetParent 之后立即 GetParent 会返回 0，
            // 但窗口其实已经挂成子窗口了（EnumChildWindows 能列出来）。
            // 用 WindowTree.IsChildOf 做"枚举确认"，这个判据是可靠的。
            bool becameChild = WindowTree.IsChildOf(_hwnd, tray);
            _embedParent = becameChild ? tray : IntPtr.Zero;

            if (becameChild)
            {
                // 切成真正的子窗口样式，这样它由任务栏负责绘制/裁剪，
                // 天然排在任务栏之上、随任务栏一起移动和自动隐藏。
                long style = GetWindowLongPtr(_hwnd, GWL_STYLE).ToInt64();
                style = (style | WS_CHILD | WS_CLIPSIBLINGS) & ~(long)WS_POPUP;
                SetWindowLongPtr(_hwnd, GWL_STYLE, new IntPtr(style));
                _embedded = true;

                // 嵌入后坐标按"父窗口客户区"解释，不再需要额外偏移
                _coordOffsetY = 0;
                TrayContext.Log($"pill: 已嵌入任务栏（真子窗口）err={setParentErr}");
            }
            else
            {
                _coordOffsetY = ProbeCoordOffsetY(tray);
                TrayContext.Log($"pill: SetParent 未生效 err={setParentErr} 坐标偏移={_coordOffsetY} → 走置顶浮窗");
            }
        }

        // 置顶模式的补充手段：把 owner 设成任务栏。
        // owner 关系同样能让窗口排在 owner 之上，而且比 SetParent 更容易成功。
        _owned = false;
        if (!_embedded && tray != IntPtr.Zero)
        {
            SetWindowLongPtr(_hwnd, GWLP_HWNDPARENT, tray);
            _owned = GetWindow(_hwnd, GW_OWNER) == tray;
            TrayContext.Log($"pill: owner={( _owned ? "taskbar" : "none" )}");
        }

        ModeName = _embedded ? "embed" : "overlay";
        ShowWindow(_hwnd, SW_SHOWNOACTIVATE);
        EnsureMouseHook();
        Render();
    }

    /// <summary>健康检查：explorer 重启 / 首次失败时重挂；置顶模式顺便压到任务栏之上。</summary>
    public void Tick()
    {
        if (!Alive)
        {
            // 失败退避：Create 连续失败时不要每 500ms 重试一次并打一行日志
            if (Environment.TickCount64 - _lastCreateFailTick < 5000) return;
            Create();
            if (!Alive) _lastCreateFailTick = Environment.TickCount64;
            return;
        }

        // 全屏应用（游戏/视频/演示）时整体隐藏贴片，不干扰画面。
        // 放在最前面：隐藏期间不需要做任何 Z 序/布局维护。
        if (ApplyFullScreenVisibility()) return;

        IntPtr tray = FindWindow("Shell_TrayWnd", null);

        if (_embedded)
        {
            // 注意：这里必须用 WindowTree.IsChildOf，不能用 GetParent != tray。
            // 跨进程 SetParent 成功后 GetParent 仍返回 0（本机实测），
            // 用 GetParent 判断会每 500ms 都误判"掉了"，然后 Rebuild —— 嵌入等于白做。
            if (tray == IntPtr.Zero || !WindowTree.IsChildOf(_hwnd, tray))
            {
                Rebuild();
                return;
            }
        }
        else if (!_owned)
        {
            KeepAbove();         // 只在真被压住时才抬回去，平时一动不动
        }
        else if (tray == IntPtr.Zero || GetWindow(_hwnd, GW_OWNER) != tray)
        {
            Rebuild();           // explorer 重启后 owner 没了，重挂
            return;
        }

        // 任务栏自动隐藏/改位置/改高度时重新对齐（内容不变，只挪窗口）。
        // 注意这里用 Place 而不是 MoveWindow：Place 会顺手把 Z 序也摆正。
        var l = Layout();
        if (l != _lastLayout)
        {
            _lastLayout = l;
            Place(l.x, l.y, l.w, l.h);
        }

        // 嵌入模式下 Z 序由父窗口（任务栏）负责，不要再去 SetWindowPos(HWND_TOPMOST)，
        // 那会把子窗口从任务栏的合成层里拽出来，反而更糟。
        if (!_embedded) EnsureOnTop();
    }

    void Rebuild()
    {
        if (Alive) DestroyWindow(_hwnd);
        _hwnd = IntPtr.Zero;
        Create();
    }

    /// <summary>当前是否因为全屏应用而隐藏着。</summary>
    bool _hiddenByFullScreen;

    /// <summary>
    /// 全屏应用时隐藏、退出全屏后恢复。
    /// 返回 true 表示"当前处于隐藏状态，本次 Tick 不需要再做别的维护工作"。
    ///
    /// 隐藏用"移出屏幕 + SW_HIDE"双保险：
    ///   实测只调 SW_HIDE 时，分层窗口（WS_EX_LAYERED + UpdateLayeredWindow）
    ///   的 IsWindowVisible 不一定立刻翻转，画面也可能残留一帧。
    ///   把窗口摆到可视区域之外（x 为负的大值）能立刻让它从合成面上消失。
    /// </summary>
    bool ApplyFullScreenVisibility()
    {
        bool full = FullScreen.IsActive();

        if (full && !_hiddenByFullScreen)
        {
            _hiddenByFullScreen = true;
            ShowWindow(_hwnd, SW_HIDE);
            SetWindowPos(_hwnd, IntPtr.Zero, -32000, -32000, 1, 1,
                SWP_NOZORDER | SWP_NOACTIVATE);
            TrayContext.Log("pill: 检测到全屏应用，隐藏贴片");
        }
        else if (!full && _hiddenByFullScreen)
        {
            _hiddenByFullScreen = false;
            // 重置布局缓存，逼下一次 Render/Tick 重新算位置并 Place 回去
            _lastLayout = (-1, -1, -1, -1);
            Render();                    // Render 内部会 Layout + Place + 重新提交位图
            ShowWindow(_hwnd, SW_SHOWNOACTIVATE);
            TrayContext.Log("pill: 退出全屏，恢复贴片");
        }

        return _hiddenByFullScreen;
    }

    /// <summary>是否有可见窗口压在贴片上面（按 Z 序自上而下走，走到自己就停）。
    /// 不用 WindowFromPoint：贴片中间是透明的，分层窗口的透明处对命中测试是穿透的，会误判。</summary>
    bool Covered()
    {
        if (!GetWindowRect(_hwnd, out RECT me)) return false;
        uint myPid = (uint)Environment.ProcessId;

        for (IntPtr h = GetTopWindow(IntPtr.Zero); h != IntPtr.Zero && h != _hwnd; h = GetWindow(h, GW_HWNDNEXT))
        {
            if (!IsWindowVisible(h)) continue;
            GetWindowThreadProcessId(h, out uint pid);
            if (pid == myPid) continue;
            if (!GetWindowRect(h, out RECT r)) continue;
            if (r.Right <= r.Left || r.Bottom <= r.Top) continue;
            if (r.Left >= me.Right || r.Right <= me.Left || r.Top >= me.Bottom || r.Bottom <= me.Top) continue;
            return true;
        }
        return false;
    }

    /// <summary>被压住时才重新置顶（不带 SWP_SHOWWINDOW，避免多一次显示/重绘）。</summary>
    void KeepAbove()
    {
        if (!Alive || _embedded) return;
        if (_lastLayout.h <= 0) return;      // 还没排版/首帧，此时窗口在 0,0，判断"被压住"没有意义
        if (!Covered()) return;
        SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        if (_raises++ < 3) TrayContext.Log($"pill: raise #{_raises}（被压住，重新置顶）");
    }

    // 前台窗口一变（点任务栏、切程序）立刻补一次，而不是等下一个 500 ms tick
    static void OnForegroundChange(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        TaskbarPill[] all;
        lock (Instances) all = Instances.Values.ToArray();   // 空数组直接走完事，不再无锁先读 Count
        foreach (var p in all)
        {
            try { p.KeepAbove(); } catch { /* 回调里不能抛 */ }
        }
    }

    public void Update(int percent, bool charging, bool connected)
    {
        if (_percent == percent && _charging == charging && _connected == connected) return;
        _percent = percent;
        _charging = charging;
        _connected = connected;
        Render();
    }

    // ——— 布局（全部按任务栏高度取比例，规避跨进程 SetParent 造成的 DPI 虚拟化） ———

    (int x, int y, int w, int h) Layout()
    {
        IntPtr tray = FindWindow("Shell_TrayWnd", null);
        if (tray == IntPtr.Zero || !GetWindowRect(tray, out RECT trayRect)) return (0, 0, 10, 10);

        int trayH = trayRect.Bottom - trayRect.Top;
        int h = Math.Max(10, (int)(trayH * 0.48));
        int iconW = IconWidth(h);
        int gapIcon = IconGap(h);
        int gap = Math.Max(4, (int)(trayH * 0.12));

        var (text, _) = Content();
        int textW;
        using (var probe = new Bitmap(1, 1, PixelFormat.Format32bppArgb))
        using (var g = Graphics.FromImage(probe))
        using (var f = FontFor(h))
        {
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            textW = (int)Math.Ceiling(g.MeasureString(text, f, int.MaxValue, StringFormat.GenericTypographic).Width);
        }

        int w = iconW + gapIcon + textW + 2;

        int screenLeft = trayRect.Right - w - gap;
        IntPtr notify = FindWindowEx(tray, IntPtr.Zero, "TrayNotifyWnd", null);
        if (notify != IntPtr.Zero && GetWindowRect(notify, out RECT nr) && nr.Right > nr.Left)
            screenLeft = nr.Left - w - gap;

        int screenTop = trayRect.Top + (trayH - h) / 2;

        // Layout 一律返回**屏幕坐标**；子窗口所需的客户区换算统一在 Place() 里做，
        // 避免两处各算一次导致偏移叠加。
        return (screenLeft, screenTop, w, h);
    }

    (string text, bool bolt) Content() => ContentOf(_percent, _charging, _connected);

    static (string text, bool bolt) ContentOf(int percent, bool charging, bool connected)
    {
        if (!connected) return ("--", false);
        return ((percent < 0 ? "--" : percent.ToString()) + "%", charging);
    }

    static Font FontFor(int h) =>
        new("Segoe UI", Math.Max(7f, h * 0.56f), FontStyle.Regular, GraphicsUnit.Pixel);

    static int IconWidth(int h) => Math.Max(7, (int)Math.Round(h * 0.60));
    static int IconGap(int h) => Math.Max(3, (int)Math.Round(h * 0.26));

    // ——— 绘制 ———

    void Render()
    {
        if (!Alive) return;

        var (x, y, w, h) = Layout();

        if (_logged < 3)
        {
            // 一次把"坐标系统"的底细全打出来，避免再来回猜
            IntPtr trayDbg = FindWindow("Shell_TrayWnd", null);
            string trayRect = "?";
            if (trayDbg != IntPtr.Zero && GetWindowRect(trayDbg, out RECT tdbg))
                trayRect = $"({tdbg.Left},{tdbg.Top})-({tdbg.Right},{tdbg.Bottom}) 高{tdbg.Bottom - tdbg.Top}";
            TrayContext.Log($"pill: 诊断 Layout=({x},{y}) {w}x{h}  任务栏={trayRect}  " +
                            $"任务栏DPI={GetDpiForWindow(trayDbg)} 探针DPI={GetDpiForWindow(_hwnd)}  " +
                            $"线程感知={GetAwarenessFromDpiAwarenessContext(GetThreadDpiAwarenessContext())}  " +
                            $"模式={ModeName} parent=0x{_embedParent.ToInt64():X}");
        }

        Place(x, y, w, h);
        if (!_embedded) EnsureOnTop();   // 子窗口的 Z 序归任务栏管
        _lastLayout = (x, y, w, h);

        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);
            Paint(g, w, h);
        }

        if (_layeredOk)
        {
            bool ok = Submit(bmp);
            if (_logged++ < 3)
            {
                GetWindowRect(_hwnd, out RECT wr);
                TrayContext.Log($"pill: render ulw={ok} mode={ModeName} " +
                                $"想要=({x},{y}) {w}x{h}  实际=({wr.Left},{wr.Top}) {wr.Right - wr.Left}x{wr.Bottom - wr.Top}");
            }
            if (ok) return;
            _layeredOk = false;   // 极端情况退回 WM_PAINT 常规绘制
        }    }

    /// <summary>
    /// 把贴片摆到 (x,y) —— 这两个值是**屏幕坐标**（Layout 已按 parent 换算过）。
    ///
    /// 注意：子窗口的 SetWindowPos 用的是相对父窗口客户区的坐标，
    /// 所以 embed 模式下要把屏幕坐标 MapWindowPoints 回父窗口。
    /// 这里不再用"比例反算"那套猜测（历史方案会把固定的父窗口偏移误判成缩放，
    /// 得出"坐标被缩放 x6.83"这种荒谬结论）。
    /// </summary>
    void Place(int x, int y, int w, int h)
    {
        // 用 Create() 阶段探测出来的坐标语义直接换算，一次到位；
        // 再用"回读-修正"循环兜底（最多 4 次），防止窗口被 explorer 重置过样式。
        int px = x, py = y - _coordOffsetY;

        for (int attempt = 0; attempt < 4; attempt++)
        {
            SetWindowPos(_hwnd, IntPtr.Zero, px, py, w, h, SWP_NOZORDER | SWP_NOACTIVATE);
            if (!GetWindowRect(_hwnd, out RECT got)) return;

            int dx = got.Left - x;
            int dy = got.Top - y;
            if (Math.Abs(dx) <= 2 && Math.Abs(dy) <= 2)
            {
                if (attempt > 0)
                    TrayContext.Log($"pill: 坐标修正 {attempt} 次后对齐 目标({x},{y}) 实际({got.Left},{got.Top}) 偏移={_coordOffsetY}");
                return;
            }

            // 偏差如果正好等于（新增的）客户区原点偏移，就把它吸收进 _coordOffsetY
            if (attempt == 0 && Math.Abs(dy) > 100)
            {
                _coordOffsetY += dy;
                py -= dy;
            }
            else
            {
                px -= dx;
                py -= dy;
            }
        }

        GetWindowRect(_hwnd, out RECT fin);
        TrayContext.Log($"pill: 坐标未收敛 目标({x},{y}) 实际({fin.Left},{fin.Top}) 偏移={_coordOffsetY}");
    }

    /// <summary>
    /// 探测这个窗口的 SetWindowPos 到底按"屏幕坐标"还是"父窗口客户区坐标"解释。
    ///
    /// 做法：在屏幕坐标 (0, y0) 处摆一次，回读 GetWindowRect。
    ///   - 报 y≈y0        → 屏幕坐标，偏移 0
    ///   - 报 y≈y0+offset → 客户区坐标，offset 就是父窗口客户区原点的屏幕 Y
    /// 用 y0=200 这种远离任务栏的值，避免和任务栏自身位置混淆。
    /// </summary>
    int ProbeCoordOffsetY(IntPtr tray)
    {
        const int y0 = 200;

        SetWindowPos(_hwnd, IntPtr.Zero, 0, y0, 10, 10, SWP_NOZORDER | SWP_NOACTIVATE);
        if (!GetWindowRect(_hwnd, out RECT probe)) return 0;

        int delta = probe.Top - y0;
        // 容差：差 2px 以内视为无偏移
        if (Math.Abs(delta) <= 2) return 0;

        // 用父窗口客户区原点核对一次，确认这个 delta 确实是"客户区原点"
        if (_embedParent != IntPtr.Zero)
        {
            var origin = new POINT { X = 0, Y = 0 };
            if (ClientToScreen(_embedParent, ref origin) && Math.Abs(origin.Y - delta) <= 4)
                return origin.Y;
        }
        if (tray != IntPtr.Zero)
        {
            var origin = new POINT { X = 0, Y = 0 };
            if (ClientToScreen(tray, ref origin) && Math.Abs(origin.Y - delta) <= 4)
                return origin.Y;
        }
        return delta;
    }

    /// <summary>
    /// 确保贴片排在任务栏之上。
    ///
    /// 实测问题：贴片位置算对了（rect 正是 2004,1547），但 WindowFromPoint 命中的是
    /// Shell_TrayWnd —— 任务栏压在上面，于是鼠标右键根本点不到贴片，菜单弹不出来；
    /// 视觉上也时有时无。单纯 WS_EX_TOPMOST 不够（任务栏自己也是 topmost 且更靠前）。
    ///
    /// 这里用 SetWindowPos(HWND_TOPMOST) 显式提到 topmost 组的最前面。
    /// </summary>
    void EnsureOnTop()
    {
        if (_hwnd == IntPtr.Zero) return;
        SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    void Paint(Graphics g, int w, int h) =>
        PaintContent(g, w, h, IsLightTaskbar(), _percent, _charging, _connected);

    // ——— 低层鼠标钩子：不依赖窗口 Z 序 ———

    /// <summary>
    /// 贴片被任务栏压在下面时（实测 WindowFromPoint 命中 Shell_TrayWnd），窗口根本收不到鼠标消息，
    /// 右键就完全没反应。这里装 WH_MOUSE_LL，只看光标是否落在我们自己的矩形里，
    /// 命中才处理，完全不依赖 Z 序。
    /// </summary>
    static IntPtr _mouseHook = IntPtr.Zero;
    static LowLevelMouseProc? _mouseProc;   // 必须静态持有，否则 GC 回收后回调进野指针

    const int WH_MOUSE_LL = 14;
    const int WM_LBUTTONUP_LL = 0x0202;
    const int WM_RBUTTONUP_LL = 0x0205;

    [StructLayout(LayoutKind.Sequential)]
    struct MSLLHOOKSTRUCT
    {
        public int ptX, ptY;
        public uint mouseData, flags, time;
        public IntPtr dwExtraInfo;
    }

    delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc proc, IntPtr hMod, uint threadId);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    static void EnsureMouseHook()
    {
        if (_mouseHook != IntPtr.Zero && _mouseHook != new IntPtr(-1)) return;

        _mouseProc = OnLowLevelMouse;
        _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, GetModuleHandle(null), 0);
        if (_mouseHook == IntPtr.Zero || _mouseHook == new IntPtr(-1))
        {
            TrayContext.Log($"pill: 鼠标钩子安装失败 err={Marshal.GetLastWin32Error()}");
            _mouseHook = IntPtr.Zero;
        }
        else
        {
            TrayContext.Log("pill: 鼠标钩子已安装");
        }
    }

    static IntPtr OnLowLevelMouse(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = wParam.ToInt32();
            if (msg == WM_LBUTTONUP_LL || msg == WM_RBUTTONUP_LL)
            {
                var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                TaskbarPill? hit = null;

                lock (Instances)
                {
                    foreach (var p in Instances.Values)
                    {
                        if (p.HitTestScreen(info.ptX, info.ptY)) { hit = p; break; }
                    }
                }

                if (hit != null)
                {
                    // 只投递一条消息，真正的业务在 UI 线程的 WndProc 里执行：
                    //   · 低层钩子回调里做重活会撞上系统 300ms 超时（钩子会被静默摘掉）
                    //     —— 所以这个回调里**不能写日志**（文件 I/O 本身就可能超时）
                    //   · 统一到一个入口才好对"钩子 + 窗口消息"的重复上报去重
                    bool ok = PostMessage(hit._hwnd,
                        msg == WM_LBUTTONUP_LL ? (uint)WM_APP_PILL_LEFT : (uint)WM_APP_PILL_RIGHT,
                        IntPtr.Zero, IntPtr.Zero);
                    if (!ok)
                        TrayContext.Log("pill: 钩子投递点击失败（窗口可能刚被重建），本次点击丢失");
                }
            }
        }
        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    /// <summary>光标（屏幕坐标）是否落在贴片矩形里。</summary>
    bool HitTestScreen(int sx, int sy)
    {
        if (_hwnd == IntPtr.Zero) return false;
        if (!GetWindowRect(_hwnd, out RECT r)) return false;
        return sx >= r.Left && sx < r.Right && sy >= r.Top && sy < r.Bottom;
    }

    /// <summary>无底色、无边框：只有一个细线鼠标图标 + 百分比，直接融进任务栏。</summary>
    public static void PaintContent(Graphics g, int w, int h, bool light, int percent, bool charging, bool connected)
    {
        Color accent = AccentOf(light, percent, charging, connected);
        Color ink = light ? Color.FromArgb(236, 0x1B, 0x1B, 0x1B) : Color.FromArgb(240, 0xF3, 0xF3, 0xF3);

        var (text, bolt) = ContentOf(percent, charging, connected);

        int iconW = IconWidth(h);
        int iconH = Math.Max(8, (int)(h * 0.84));
        var iconRect = new RectangleF(1, (h - iconH) / 2f, iconW, iconH);
        PaintMouse(g, iconRect, accent, bolt);

        using var font = FontFor(h);
        using var sf = new StringFormat(StringFormat.GenericTypographic)
        {
            Alignment = StringAlignment.Near,
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.NoClip,
        };
        using var tb = new SolidBrush(ink);
        float tx = iconRect.Right + IconGap(h);
        g.DrawString(text, font, tb, new RectangleF(tx, 0, w - tx, h), sf);
    }

    /// <summary>给 --icon 预览用的整块内容位图（不含窗口）。</summary>
    public static Bitmap RenderPreview(int trayHeight, int percent, bool charging, bool connected, bool light)
    {
        int h = Math.Max(10, (int)(trayHeight * 0.48));
        var (text, _) = ContentOf(percent, charging, connected);
        int textW;
        using (var probe = new Bitmap(1, 1, PixelFormat.Format32bppArgb))
        using (var g0 = Graphics.FromImage(probe))
        using (var f0 = FontFor(h))
        {
            g0.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            textW = (int)Math.Ceiling(g0.MeasureString(text, f0, int.MaxValue, StringFormat.GenericTypographic).Width);
        }

        int w = IconWidth(h) + IconGap(h) + textW + 2;
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);
            PaintContent(g, w, h, light, percent, charging, connected);
        }
        return bmp;
    }

    /// <summary>细线鼠标：圆角机身 + 顶部按键分隔线（充电时在机身里换成小闪电）。</summary>
    static void PaintMouse(Graphics g, RectangleF r, Color color, bool charging)
    {
        float sw = Math.Max(1.1f, r.Width * 0.11f);
        using var pen = new Pen(color, sw)
        {
            LineJoin = LineJoin.Round,
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            Alignment = PenAlignment.Center,
        };

        var body = RectangleF.Inflate(r, -sw / 2f, -sw / 2f);
        using (var path = BatteryIcon.RoundedRect(body, body.Width * 0.47f))
        {
            g.DrawPath(pen, path);
        }

        float midX = body.X + body.Width / 2f;
        g.DrawLine(pen, midX, body.Y + sw * 0.3f, midX, body.Y + body.Height * 0.26f);

        if (charging)
        {
            float bw = body.Width * 0.52f;
            float bh = body.Height * 0.34f;
            using var bp = BoltPath(new RectangleF(midX - bw / 2f, body.Y + body.Height * 0.38f, bw, bh));
            using var bb = new SolidBrush(color);
            g.FillPath(bb, bp);
        }
    }

    static GraphicsPath BoltPath(RectangleF r)
    {
        float x = r.X, y = r.Y, w = r.Width, h = r.Height;
        return new GraphicsPath(new[]
        {
            new PointF(x + w * 0.58f, y),
            new PointF(x + w * 0.10f, y + h * 0.58f),
            new PointF(x + w * 0.42f, y + h * 0.58f),
            new PointF(x + w * 0.22f, y + h),
            new PointF(x + w * 0.90f, y + h * 0.40f),
            new PointF(x + w * 0.52f, y + h * 0.40f),
        }, new[]
        {
            (byte)PathPointType.Start, (byte)PathPointType.Line, (byte)PathPointType.Line,
            (byte)PathPointType.Line, (byte)PathPointType.Line, (byte)PathPointType.Line,
        });
    }

    static bool IsLightTaskbar() => Theme.IsLightTaskbar();

    Color Accent(bool light) => AccentOf(light, _percent, _charging, _connected);

    static Color AccentOf(bool light, int percent, bool charging, bool connected)
        => Theme.Accent(light, percent, charging, connected);

    bool Submit(Bitmap bmp) => SubmitLayered(_hwnd, bmp);

    /// <summary>
    /// 把位图提交到分层窗口（UpdateLayeredWindow）。贴片和右键菜单共用这一份实现，
    /// 免得两边各写一套、一边能成一边不成。
    /// </summary>
    public static bool SubmitLayered(IntPtr hwnd, Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        var hdr = new BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = w,
            biHeight = -h,
            biPlanes = 1,
            biBitCount = 32,
        };

        IntPtr screenDc = GetDC(IntPtr.Zero);
        IntPtr memDc = CreateCompatibleDC(screenDc);
        IntPtr dib = CreateDIBSection(memDc, ref hdr, 0, out IntPtr bits, IntPtr.Zero, 0);
        if (dib == IntPtr.Zero || bits == IntPtr.Zero)
        {
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
            return false;
        }

        IntPtr old = SelectObject(memDc, dib);
        bool ok;
        try
        {
            BitmapData data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                int rowBytes = w * 4;
                var row = new byte[rowBytes];
                for (int y = 0; y < h; y++)
                {
                    Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, rowBytes);
                    for (int i = 0; i < rowBytes; i += 4)
                    {
                        byte a = row[i + 3];
                        if (a == 0) { row[i] = row[i + 1] = row[i + 2] = 0; }
                        else if (a != 255)
                        {
                            row[i] = (byte)(row[i] * a / 255);
                            row[i + 1] = (byte)(row[i + 1] * a / 255);
                            row[i + 2] = (byte)(row[i + 2] * a / 255);
                        }
                    }
                    Marshal.Copy(row, 0, bits + y * rowBytes, rowBytes);
                }
            }
            finally
            {
                bmp.UnlockBits(data);
            }

            var size = new SIZE { Cx = w, Cy = h };
            var src = new POINT { X = 0, Y = 0 };
            var blend = new BLENDFUNCTION { BlendOp = AC_SRC_OVER, SourceConstantAlpha = 255, AlphaFormat = AC_SRC_ALPHA };

            ok = UpdateLayeredWindow(hwnd, screenDc, IntPtr.Zero, ref size, memDc, ref src, 0, ref blend, ULW_ALPHA);
        }
        finally
        {
            // 任何异常路径也要把 GDI 对象还回去，否则每次失败漏一组 DC/位图
            SelectObject(memDc, old);
            DeleteObject(dib);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
        return ok;
    }

    // ——— 消息 ———

    /// <summary>
    /// 点击的唯一分发点。
    ///
    /// 同一次点击可能被上报两遍：低层钩子一遍（它看的是光标坐标，不看 Z 序），
    /// 窗口消息一遍（贴片真被点到时）。不去重的话右键会连弹两个菜单、
    /// 左键会连着读两次电量。这里按"同类点击 300ms 内只认一次"过滤。
    /// </summary>
    void DispatchClick(bool left)
    {
        long now = Environment.TickCount64;
        if (left)
        {
            if (now - _lastLeftTick < 300) return;
            _lastLeftTick = now;
            TrayContext.Log("pill: 左键");
            try { LeftClick?.Invoke(); } catch (Exception ex) { TrayContext.Log("pill: 左键处理异常 " + ex.Message); }
        }
        else
        {
            if (now - _lastRightTick < 300) return;
            _lastRightTick = now;
            TrayContext.Log("pill: 右键");
            try { RightClick?.Invoke(); } catch (Exception ex) { TrayContext.Log("pill: 右键处理异常 " + ex.Message); }
        }
    }

    static IntPtr WndProcStatic(IntPtr hwnd, uint msg, IntPtr w, IntPtr l)
    {
        TaskbarPill? self;
        // 钩子线程会并发枚举 Instances，这里读取也必须加锁，否则可能读到正在修改的字典
        lock (Instances) Instances.TryGetValue(hwnd, out self);
        return self != null ? self.WndProc(hwnd, msg, w, l) : DefWindowProc(hwnd, msg, w, l);
    }

    IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr w, IntPtr l)
    {
        switch (msg)
        {
            // 强制参与命中测试：分层窗口在某些情况下会被 WindowFromPoint 忽略，
            // 显式返回 HTCLIENT 让鼠标事件落到自己身上，而不是被下面的任务栏吃掉。
            case WM_NCHITTEST:
                return new IntPtr(HTCLIENT);

            // 钩子投递过来的点击（贴片被任务栏压住时唯一可靠的入口）
            case WM_APP_PILL_LEFT:
                DispatchClick(left: true);
                return IntPtr.Zero;
            case WM_APP_PILL_RIGHT:
                DispatchClick(left: false);
                return IntPtr.Zero;

            case WM_LBUTTONUP:
                DispatchClick(left: true);
                return IntPtr.Zero;
            case WM_RBUTTONUP:
            case WM_CONTEXTMENU:
                // 一次右键会同时来 WM_RBUTTONUP 和 WM_CONTEXTMENU，去重交给 DispatchClick
                DispatchClick(left: false);
                return IntPtr.Zero;
            case WM_MOUSEACTIVATE:
                // 和菜单一样：带 WS_EX_NOACTIVATE 的窗口返回 MA_ACTIVATE 仍会抢焦点，
                // 必须显式 MA_NOACTIVATE，点击才会被当成普通点击投递过来。
                return new IntPtr(MA_NOACTIVATE);
            case WM_SETCURSOR:
                SetCursor(LoadCursor(IntPtr.Zero, new IntPtr(IDC_HAND)));
                return new IntPtr(1);
            case WM_ERASEBKGND:
                return new IntPtr(1);
            case WM_PAINT when !_layeredOk:
                {
                    // 必须用 BeginPaint/EndPaint：EndPaint 才会 ValidateRect 把更新区域置为有效。
                    // 原来用 GetDC 画而不验证区域，更新区域永远悬着 → WM_PAINT 无限重发刷爆 CPU。
                    IntPtr dc = BeginPaint(hwnd, out PAINTSTRUCT ps);
                    try
                    {
                        GetClientRect(hwnd, out RECT cr);
                        using (var g = Graphics.FromHdc(dc))
                        {
                            g.Clear(IsLightTaskbar() ? Color.FromArgb(0xF3, 0xF4, 0xF6) : Color.FromArgb(0x20, 0x20, 0x20));
                            Paint(g, cr.Right - cr.Left, cr.Bottom - cr.Top);
                        }
                    }
                    finally
                    {
                        EndPaint(hwnd, ref ps);
                    }
                    return IntPtr.Zero;
                }
            case WM_DESTROY:
                lock (Instances) Instances.Remove(hwnd);
                return IntPtr.Zero;
        }
        return DefWindowProc(hwnd, msg, w, l);
    }


    public void Dispose()
    {
        _tick.Stop();
        _tick.Dispose();

        // 全局钩子必须显式卸掉：留着的话进程退出前每次鼠标/前台切换都要回调进来
        if (_mouseHook != IntPtr.Zero)
        {
            try { UnhookWindowsHookEx(_mouseHook); } catch { }
            _mouseHook = IntPtr.Zero;
        }
        _mouseProc = null;

        if (_winEventHook != IntPtr.Zero)
        {
            try { UnhookWinEvent(_winEventHook); } catch { }
            _winEventHook = IntPtr.Zero;
        }
        _winEventProc = null;

        if (_hwnd != IntPtr.Zero)
        {
            lock (Instances) Instances.Remove(_hwnd);
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
    }
}



