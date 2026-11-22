using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

// 模拟主程序的嵌入流程，验证两件事：
//   1) 跨进程 SetParent 后，EnumChildWindows 能否看到自己（即"真嵌入"）
//   2) 切成 WS_CHILD 后，SetWindowPos 是否按任务栏客户区解释
// 这个探针不依赖主程序，可单独运行。
internal static class EmbedProbe
{
    [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr c);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string c, string t);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateWindowEx(int ex, string cls, string title, int style,
        int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetParent(IntPtr child, IntPtr parent);
    [DllImport("user32.dll")] static extern IntPtr GetParent(IntPtr h);
    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr p);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint f);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr h, ref POINT p);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string n);

    delegate bool EnumProc(IntPtr h, IntPtr p);

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }

    const int WS_POPUP = unchecked((int)0x80000000);
    const int WS_CHILD = 0x40000000;
    const int WS_EX_LAYERED = 0x00080000;
    const int WS_EX_TOOLWINDOW = 0x00000080;
    const int WS_EX_NOACTIVATE = 0x08000000;
    const uint SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;

    static bool IsChildByEnum(IntPtr child, IntPtr parent)
    {
        bool found = false;
        EnumChildWindows(parent, (h, _) => { if (h == child) { found = true; return false; } return true; }, IntPtr.Zero);
        return found;
    }

    static int Main()
    {
        SetProcessDpiAwarenessContext(new IntPtr(-4));

        IntPtr tray = FindWindow("Shell_TrayWnd", null);
        if (tray == IntPtr.Zero) { Console.WriteLine("找不到任务栏"); return 1; }

        GetWindowRect(tray, out RECT tr);
        var origin = new POINT { X = 0, Y = 0 };
        ClientToScreen(tray, ref origin);
        Console.WriteLine($"任务栏 屏幕矩形=({tr.L},{tr.T})-({tr.R},{tr.B})  客户区原点=({origin.X},{origin.Y})");

        IntPtr notify = FindWindowExW(tray, IntPtr.Zero, "TrayNotifyWnd", null);
        int pillW = 68, pillH = 34;
        int gap = 8;
        int targetScreenX = tr.R - pillW - gap;
        int targetScreenY = tr.T + (tr.B - tr.T - pillH) / 2;
        if (notify != IntPtr.Zero && GetWindowRect(notify, out RECT nr))
        {
            targetScreenX = nr.L - pillW - gap;
            Console.WriteLine($"  TrayNotifyWnd=({nr.L},{nr.T})-({nr.R},{nr.B}) → 目标屏幕 X={targetScreenX}");
        }
        Console.WriteLine($"目标屏幕坐标 = ({targetScreenX},{targetScreenY})");

        // 建一个和贴片同样风格的分层窗口
        IntPtr h = CreateWindowEx(WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
            "STATIC", "embed-probe", WS_POPUP, 0, 0, pillW, pillH,
            IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
        if (h == IntPtr.Zero) { Console.WriteLine("建窗口失败"); return 2; }
        Console.WriteLine($"\n探针窗口=0x{h.ToInt64():X}");

        // ---- 步骤 1：SetParent ----
        SetParent(h, tray);
        int err = Marshal.GetLastWin32Error();
        Console.WriteLine($"\n[1] SetParent(tray)  err={err}");
        Console.WriteLine($"    GetParent      = 0x{GetParent(h).ToInt64():X}   （可能为 0，不可靠）");
        bool isChild = IsChildByEnum(h, tray);
        Console.WriteLine($"    EnumChildWindows 里能找到 = {isChild}   ← 这才是可靠判据");

        if (!isChild)
        {
            Console.WriteLine("\n结论：这个环境下跨进程嵌入不成立，只能走置顶浮窗。");
            DestroyWindow(h);
            return 3;
        }

        // ---- 步骤 2：切 WS_CHILD ----
        long style = GetWindowLongPtr(h, -16).ToInt64();
        style = (style | WS_CHILD) & ~(long)WS_POPUP;
        SetWindowLongPtr(h, -16, new IntPtr(style));
        Console.WriteLine($"\n[2] 已切 WS_CHILD，GetParent=0x{GetParent(h).ToInt64():X}");

        // ---- 步骤 3：按客户区坐标摆放 ----
        int cx = targetScreenX - origin.X;
        int cy = targetScreenY - origin.Y;
        Console.WriteLine($"\n[3] 目标客户区坐标 = ({cx},{cy})");
        SetWindowPos(h, IntPtr.Zero, cx, cy, pillW, pillH, SWP_NOZORDER | SWP_NOACTIVATE);
        GetWindowRect(h, out RECT rr);
        Console.WriteLine($"    摆完屏幕矩形 = ({rr.L},{rr.T})-({rr.R},{rr.B})");
        Console.WriteLine($"    与目标屏幕坐标偏差 = ({rr.L - targetScreenX},{rr.T - targetScreenY})");

        // ---- 步骤 4：可见性 ----
        ShowWindow(h, 5);
        Thread.Sleep(300);
        Console.WriteLine($"\n[4] 显示后 IsWindowVisible = {IsWindowVisible(h)}");
        Console.WriteLine($"    仍在任务栏子窗口列表 = {IsChildByEnum(h, tray)}");

        bool ok = isChild
               && Math.Abs(rr.L - targetScreenX) <= 2
               && Math.Abs(rr.T - targetScreenY) <= 2
               && IsWindowVisible(h);
        Console.WriteLine();
        Console.WriteLine(ok
            ? "★★ 嵌入可行：真子窗口 + 坐标正确 + 可见"
            : "嵌入不完全：请把上面各项发给开发者");

        Thread.Sleep(1500);
        DestroyWindow(h);
        return ok ? 0 : 4;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr FindWindowExW(IntPtr p, IntPtr a, string c, string t);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr SetWindowLongPtr(IntPtr h, int i, IntPtr v);
}

