using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

// 探针：任务栏窗口的层级结构 + 各种"嵌入"手段在这个系统上到底能不能成。
// 目标：搞清楚 win11 26300 的 Shell_TrayWnd 是否允许跨进程 SetParent，
// 以及如果不行，还有哪些替代落点（比如 TrayNotifyWnd、或自己挂在任务栏子窗口上）。
internal static class TrayProbe
{
    [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr c);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string c, string t);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr p, IntPtr after, string c, string t);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateWindowEx(int ex, string cls, string title, int style,
        int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetParent(IntPtr child, IntPtr parent);
    [DllImport("user32.dll")] static extern IntPtr GetParent(IntPtr h);
    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr p);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint f);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string n);

    delegate bool EnumProc(IntPtr h, IntPtr p);

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }

    const int WS_POPUP = unchecked((int)0x80000000);
    const int WS_CHILD = 0x40000000;
    const int WS_EX_LAYERED = 0x00080000;
    const int WS_EX_TOOLWINDOW = 0x00000080;
    const int WS_EX_NOACTIVATE = 0x08000000;
    const uint SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;

    static string Cls(IntPtr h)
    {
        var sb = new StringBuilder(256);
        GetClassName(h, sb, 256);
        return sb.ToString();
    }

    static void DumpChildren(IntPtr parent, string indent = "    ")
    {
        EnumChildWindows(parent, (h, _) =>
        {
            GetWindowThreadProcessId(h, out uint pid);
            GetWindowRect(h, out RECT r);
            Console.WriteLine($"{indent}'{Cls(h)}'  hwnd={h.ToInt64():X} pid={pid} vis={IsWindowVisible(h)} rect=({r.L},{r.T})-({r.R},{r.B})");
            return true;
        }, IntPtr.Zero);
    }

    static int Main()
    {
        SetProcessDpiAwarenessContext(new IntPtr(-4));
        GetWindowThreadProcessId(IntPtr.Zero, out _);

        Console.WriteLine("=== 1) 任务栏结构 ===");
        IntPtr tray = FindWindow("Shell_TrayWnd", null);
        if (tray == IntPtr.Zero) { Console.WriteLine("找不到 Shell_TrayWnd"); return 1; }
        GetWindowThreadProcessId(tray, out uint trayPid);
        GetWindowRect(tray, out RECT tr);
        GetClientRect(tray, out RECT tc);
        Console.WriteLine($"Shell_TrayWnd hwnd={tray.ToInt64():X} pid={trayPid}");
        Console.WriteLine($"  窗口矩形 ({tr.L},{tr.T})-({tr.R},{tr.B})  {tr.R - tr.L}x{tr.B - tr.T}");
        Console.WriteLine($"  客户区   {tc.R - tc.L}x{tc.B - tc.T}");
        Console.WriteLine("  子窗口:");
        DumpChildren(tray);

        Console.WriteLine();
        Console.WriteLine("=== 2) TrayNotifyWnd ===");
        IntPtr notify = FindWindowEx(tray, IntPtr.Zero, "TrayNotifyWnd", null);
        if (notify != IntPtr.Zero)
        {
            GetWindowRect(notify, out RECT nr);
            Console.WriteLine($"  hwnd={notify.ToInt64():X} rect=({nr.L},{nr.T})-({nr.R},{nr.B})");
            Console.WriteLine("  子窗口:");
            DumpChildren(notify, "      ");
        }
        else Console.WriteLine("  没有 TrayNotifyWnd");

        Console.WriteLine();
        Console.WriteLine("=== 3) 我的 PID vs 任务栏 PID ===");
        Console.WriteLine($"  本进程 pid={Environment.ProcessId}   任务栏 pid={trayPid}");
        Console.WriteLine($"  同进程={Environment.ProcessId == (int)trayPid}");

        Console.WriteLine();
        Console.WriteLine("=== 4) 尝试跨进程 SetParent 到各落点 ===");
        foreach (var (name, target) in new (string, IntPtr)[]
        {
            ("Shell_TrayWnd", tray),
            ("TrayNotifyWnd", notify),
        })
        {
            if (target == IntPtr.Zero) { Console.WriteLine($"  [{name}] 跳过（句柄为空）"); continue; }

            IntPtr child = CreateWindowEx(WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
                "STATIC", "embed-test", WS_POPUP, 0, 0, 60, 30,
                IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
            if (child == IntPtr.Zero) { Console.WriteLine($"  [{name}] 建窗口失败"); continue; }

            SetLastError(0);
            IntPtr prev = SetParent(child, target);
            int err = Marshal.GetLastWin32Error();
            IntPtr actualParent = GetParent(child);

            Console.WriteLine($"  [{name}]");
            Console.WriteLine($"    SetParent 返回={prev.ToInt64():X}  err={err}");
            Console.WriteLine($"    GetParent={actualParent.ToInt64():X}  等于目标={actualParent == target}");

            // 切成 WS_CHILD 后再测
            long style = GetWindowLongPtr(child, -16).ToInt64();
            style = (style | WS_CHILD) & ~(long)WS_POPUP;
            SetWindowLongPtr(child, -16, new IntPtr(style));
            IntPtr p2 = GetParent(child);
            Console.WriteLine($"    切 WS_CHILD 后 GetParent={p2.ToInt64():X}");

            // 摆到任务栏客户区 (10,10)，回读看是不是客户区坐标
            SetWindowPos(child, IntPtr.Zero, 10, 10, 60, 30, SWP_NOZORDER | SWP_NOACTIVATE);
            GetWindowRect(child, out RECT cr);
            Console.WriteLine($"    摆在客户区(10,10) 后屏幕矩形=({cr.L},{cr.T})  偏差=({cr.L - 10},{cr.T - 10})");

            Console.WriteLine($"    可见={IsWindowVisible(child)}");
            DestroyWindow(child);
        }

        Console.WriteLine();
        Console.WriteLine("=== 5) 结论提示 ===");
        Console.WriteLine("  若 GetParent==目标 且 可见=True，说明可以真嵌入；");
        Console.WriteLine("  若 GetParent=0 且 SetParent 返回 0，说明这个系统拒绝了跨进程嵌入。");
        return 0;
    }

    [DllImport("kernel32.dll")] static extern void SetLastError(uint e);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr SetWindowLongPtr(IntPtr h, int i, IntPtr v);
}
