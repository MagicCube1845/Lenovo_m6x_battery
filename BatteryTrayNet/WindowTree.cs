using System.Runtime.InteropServices;
using System.Text;

namespace BatteryTray;

/// <summary>
/// 判断一个窗口到底有没有成为某个父窗口的子窗口。
///
/// 为什么要单独写这个：跨进程 SetParent 之后**立刻**调 GetParent 会返回 0，
/// 但窗口其实已经挂上去了（父窗口的 EnumChildWindows 能列出来）。
/// 实测（tools/TrayProbeProj）：
///   SetParent(hwnd, Shell_TrayWnd)  → 立即 GetParent = 0，err = 0
///   但 EnumChildWindows(Shell_TrayWnd) 里已经出现我们的窗口
/// 只靠 GetParent 判断会得出"未嵌入"的错误结论，然后错误地按屏幕坐标摆放，
/// 于是贴片被移到 1528+1547=3075 这种屏幕外的位置。
/// </summary>
public static class WindowTree
{
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr p);
    [DllImport("user32.dll")] static extern IntPtr GetParent(IntPtr hwnd);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    delegate bool EnumProc(IntPtr hwnd, IntPtr param);

    const uint GA_PARENT = 1;
    const uint GA_ROOT = 2;

    /// <summary>isChild 是否是 parent 的直接或间接子窗口。</summary>
    public static bool IsChildOf(IntPtr isChild, IntPtr parent)
    {
        if (isChild == IntPtr.Zero || parent == IntPtr.Zero) return false;

        // 快路径：GetParent / GetAncestor 能拿到就直接答
        if (GetParent(isChild) == parent) return true;
        if (GetAncestor(isChild, GA_PARENT) == parent) return true;

        // 慢路径：枚举父窗口的所有子窗口（跨进程 SetParent 后 GetParent 可能滞后）
        bool found = false;
        try
        {
            EnumChildWindows(parent, (h, _) =>
            {
                if (h == isChild) { found = true; return false; }
                return true;
            }, IntPtr.Zero);
        }
        catch
        {
            // 枚举失败就按未嵌入处理
        }
        return found;
    }

    /// <summary>窗口的顶层祖先（用于判断 Z 序归属、是否被别的程序盖住等）。</summary>
    public static IntPtr Root(IntPtr hwnd) => hwnd == IntPtr.Zero ? IntPtr.Zero : GetAncestor(hwnd, GA_ROOT);

    /// <summary>窗口属于哪个进程。</summary>
    public static uint OwnerPid(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out uint pid);
        return pid;
    }
}
