using System.Runtime.InteropServices;
using System.Text;

namespace BatteryTray;

/// <summary>
/// 检测"当前是否有应用处于全屏状态"，有的话就把任务栏贴片藏起来，
/// 免得在看视频/玩游戏/做演示时有个电量数字浮在画面上。
///
/// 判据（必须全中才算全屏）：
///   ① 前台窗口不是外壳/系统 UI（桌面、任务栏、开始菜单等）
///   ② 前台窗口矩形**完全覆盖**它所在显示器的矩形
///   ③ 该窗口不是最大化窗口（最大化会保留标题栏高度，属于正常使用）
///
/// 为什么不用"任务栏是否被盖住"来判断：
///   实测（tools/FullScreenProbeProj）普通 WS_POPUP 顶层窗口即使铺满 2560x1600，
///   任务栏依然显示在最上面 —— 任务栏本身是 topmost 且 Z 序更靠前。
///   只有 DirectX 独占全屏才会真的顶掉任务栏。而日常"全屏"（浏览器全屏视频、
///   YouTube、PPT 放映）都是无边框窗口，任务栏还在，但这恰恰是用户希望隐藏贴片的场景。
///   所以判据只落到"前台窗口是否铺满整个显示器"。
/// </summary>
public static class FullScreen
{
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr hwnd);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] static extern IntPtr GetDesktopWindow();
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, StringBuilder sb, int max);

    // 注意：GetMonitorInfo 在 user32.dll，不在 shcore.dll。
    // 写成 shcore 会在调用时抛 EntryPointNotFoundException，被外层 catch 吞成 false，
    // 表现为"永远检测不到全屏"（实测踩过）。
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    const uint MONITOR_DEFAULTTONEAREST = 2;
    const uint GA_ROOT = 2;
    const int GWL_STYLE = -16;
    const int WS_MAXIMIZE = 0x01000000;

    /// <summary>外壳/系统 UI 的窗口类，它们铺满屏幕也不算"应用全屏"。</summary>
    static readonly string[] ShellClasses =
    {
        "Progman",                  // 桌面
        "WorkerW",                  // 桌面壁纸层
        "Shell_TrayWnd",            // 任务栏
        "Shell_SecondaryTrayWnd",   // 副屏任务栏
        "MultitaskingViewFrame",    // 任务视图
        "ForegroundStaging",
        "XamlExplorerHostIslandWindow",
        "TaskListThumbnailWnd",
        "NotifyIconOverflowWindow",
        "Windows.UI.Core.CoreWindow",
    };

    /// <summary>外壳进程。它们铺满屏幕不算全屏（开始菜单、搜索、输入法等）。</summary>
    static readonly string[] ShellProcesses =
    {
        "explorer",
        "StartMenuExperienceHost",
        "SearchHost",
        "ShellExperienceHost",
        "LockApp",
        "TextInputHost",
    };

    /// <summary>当前是否有应用全屏（此时应当隐藏贴片）。</summary>
    public static bool IsActive()
    {
        try
        {
            IntPtr fg = GetForegroundWindow();
            if (fg == IntPtr.Zero) return false;

            // 桌面本身
            if (fg == GetDesktopWindow() || fg == GetShellWindow()) return false;

            fg = GetAncestor(fg, GA_ROOT);
            if (fg == IntPtr.Zero) return false;

            if (!IsWindowVisible(fg) || IsIconic(fg)) return false;

            // ① 外壳窗口类
            var cls = new StringBuilder(256);
            GetClassName(fg, cls, 256);
            string klass = cls.ToString();
            foreach (var c in ShellClasses)
                if (string.Equals(klass, c, StringComparison.OrdinalIgnoreCase))
                    return false;

            // ① 外壳进程
            GetWindowThreadProcessId(fg, out uint pid);
            if (pid != 0 && IsShellProcess(pid)) return false;

            // ③ 最大化窗口不算全屏：它保留了标题栏/边框，用户还在正常用桌面
            if (IsZoomed(fg)) return false;
            long style = GetWindowLong(fg, GWL_STYLE);
            if ((style & WS_MAXIMIZE) != 0) return false;

            // ② 矩形是否铺满整个显示器
            if (!GetWindowRect(fg, out RECT wr)) return false;

            IntPtr mon = MonitorFromWindow(fg, MONITOR_DEFAULTTONEAREST);
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(mon, ref mi)) return false;

            var m = mi.rcMonitor;
            // 容差 2px；要求把显示器整个盖住
            return wr.Left <= m.Left + 2
                && wr.Top <= m.Top + 2
                && wr.Right >= m.Right - 2
                && wr.Bottom >= m.Bottom - 2;
        }
        catch (Exception ex)
        {
            LastError = ex.GetType().Name + ": " + ex.Message;
            return false;
        }
    }

    /// <summary>最近一次检测失败的原因（正常时为 null）。供诊断/日志使用。</summary>
    public static string? LastError { get; private set; }

    static bool IsShellProcess(uint pid)
    {
        try
        {
            string name;
            using (var p = System.Diagnostics.Process.GetProcessById((int)pid))
                name = p.ProcessName;
            foreach (var s in ShellProcesses)
                if (string.Equals(name, s, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }
        catch
        {
            return false;
        }
    }
}
