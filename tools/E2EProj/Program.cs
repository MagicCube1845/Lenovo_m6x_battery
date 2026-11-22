using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

// 端到端验证：主程序是否真的在全屏时隐藏贴片、退出后恢复。
// 做法：造一个铺满屏幕的前台窗口，等主程序的 watchdog（500ms）反应，
// 检查贴片窗口的可见性；再销毁窗口，确认恢复。
internal static class EndToEnd
{
    [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr c);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string c, string t);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateWindowEx(int ex, string cls, string title, int style,
        int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string n);

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }

    const int WS_POPUP = unchecked((int)0x80000000);
    const int WS_VISIBLE = 0x10000000;
    const int WS_EX_TOPMOST = 0x00000008;

    static int Main()
    {
        SetProcessDpiAwarenessContext(new IntPtr(-4));

        IntPtr pill = FindWindow("LegionBatteryPillWnd", null);
        if (pill == IntPtr.Zero) { Console.WriteLine("贴片窗口不存在（主程序没在跑？）"); return 1; }

        Console.WriteLine($"贴片初始可见 = {IsWindowVisible(pill)}");
        if (!IsWindowVisible(pill))
        {
            Console.WriteLine("初始就不可见，测试前提不成立");
            return 2;
        }

        // 铺满全屏的前台窗口
        IntPtr full = CreateWindowEx(WS_EX_TOPMOST, "STATIC", "e2e-fullscreen", WS_POPUP | WS_VISIBLE,
            0, 0, 2560, 1600, IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
        ShowWindow(full, 5);
        SetForegroundWindow(full);
        Thread.Sleep(1500);   // 等 watchdog（500ms）跑几轮

        bool hidden = !IsWindowVisible(pill);
        Console.WriteLine($"全屏时贴片可见 = {IsWindowVisible(pill)}   期望 False  {(hidden ? "PASS" : "FAIL")}");

        DestroyWindow(full);
        SetForegroundWindow(FindWindow("Shell_TrayWnd", null));
        Thread.Sleep(1500);

        bool restored = IsWindowVisible(pill);
        Console.WriteLine($"退出全屏后贴片可见 = {restored}   期望 True  {(restored ? "PASS" : "FAIL")}");

        return (hidden && restored) ? 0 : 3;
    }
}
