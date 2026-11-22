using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

// 直接测试 FullScreen.IsActive() 的判定，构造三种典型场景：
//   1) 全屏无边框窗口（覆盖整屏、非最大化）→ 应判定为全屏（隐藏贴片）
//   2) 最大化窗口（IsZoomed / WS_MAXIMIZE）→ 不应判定为全屏
//   3) 普通小窗口 → 不应判定为全屏
internal static class FullScreenProbe
{
    [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr c);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateWindowEx(int ex, string cls, string title, int style,
        int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll", SetLastError = true)] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint f);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint f);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string n);

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }

    const int WS_POPUP = unchecked((int)0x80000000);
    const int WS_VISIBLE = 0x10000000;
    const int WS_OVERLAPPEDWINDOW = 0x00CF0000;
    const int WS_EX_TOPMOST = 0x00000008;

    static string Cls(IntPtr h)
    {
        var sb = new StringBuilder(256);
        GetClassName(h, sb, 256);
        return sb.ToString();
    }

    static string NowFg() => $"'{Cls(GetAncestor(GetForegroundWindow(), 2))}'";

    static int Main()
    {
        SetProcessDpiAwarenessContext(new IntPtr(-4));

        // 先报告当前真实状态（用户桌面此刻的判定结果）
        Console.WriteLine($"【当前真实桌面】FullScreen.IsActive() = {BatteryTray.FullScreen.IsActive()}");
        Console.WriteLine($"  前台 = {NowFg()}");
        Console.WriteLine();

        int pass = 0, total = 0;

        // ---- 场景 1：全屏无边框窗口 ----
        IntPtr full = CreateWindowEx(WS_EX_TOPMOST, "STATIC", "fs-test", WS_POPUP | WS_VISIBLE,
            0, 0, 2560, 1600, IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
        SetWindowPos(full, new IntPtr(-1), 0, 0, 2560, 1600, 0x0004 | 0x0010);
        ShowWindow(full, 5);
        SetForegroundWindow(full);
        Thread.Sleep(500);
        GetWindowRect(full, out RECT fr);
        bool r1 = BatteryTray.FullScreen.IsActive();
        total++;
        Console.WriteLine($"[1] 全屏无边框 ({fr.L},{fr.T})-({fr.R},{fr.B})  前台={NowFg()}");
        Console.WriteLine($"    IsActive={r1}  期望=True  {(r1 ? "PASS" : "FAIL")}");
        if (r1) pass++;
        DestroyWindow(full);
        Thread.Sleep(300);

        // ---- 场景 2：最大化窗口 ----
        IntPtr max = CreateWindowEx(0, "STATIC", "max-test", WS_OVERLAPPEDWINDOW | WS_VISIBLE,
            100, 100, 800, 600, IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
        ShowWindow(max, 3);   // SW_MAXIMIZE
        SetForegroundWindow(max);
        Thread.Sleep(500);
        GetWindowRect(max, out RECT mr);
        bool r2 = BatteryTray.FullScreen.IsActive();
        total++;
        Console.WriteLine($"[2] 最大化窗口 ({mr.L},{mr.T})-({mr.R},{mr.B})  前台={NowFg()}");
        Console.WriteLine($"    IsActive={r2}  期望=False  {(!r2 ? "PASS" : "FAIL")}");
        if (!r2) pass++;
        DestroyWindow(max);
        Thread.Sleep(300);

        // ---- 场景 3：普通小窗口 ----
        IntPtr small = CreateWindowEx(WS_EX_TOPMOST, "STATIC", "small-test", WS_POPUP | WS_VISIBLE,
            200, 200, 400, 300, IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
        SetForegroundWindow(small);
        Thread.Sleep(500);
        GetWindowRect(small, out RECT sr);
        bool r3 = BatteryTray.FullScreen.IsActive();
        total++;
        Console.WriteLine($"[3] 小窗口 ({sr.L},{sr.T})-({sr.R},{sr.B})  前台={NowFg()}");
        Console.WriteLine($"    IsActive={r3}  期望=False  {(!r3 ? "PASS" : "FAIL")}");
        if (!r3) pass++;
        DestroyWindow(small);

        Console.WriteLine();
        Console.WriteLine($"结果: {pass}/{total} 通过");
        return pass == total ? 0 : 3;
    }
}
