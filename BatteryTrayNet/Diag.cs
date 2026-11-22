using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace BatteryTray;

/// <summary>
/// 诊断报告：把"为什么任务栏上看不到、为什么读不到电量"的每条前提都实测一遍，写进 diag.txt。
/// 必须由用户自己双击运行（正常 Medium 权限），沙箱里跑出来的结果会误导。
/// </summary>
public static class Diag
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string? title);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr SetParent(IntPtr child, IntPtr parent);
    [DllImport("user32.dll")] static extern IntPtr GetParent(IntPtr hwnd);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr SetWindowLongPtr(IntPtr h, int i, IntPtr v);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateWindowEx(int ex, string cls, string title, int style,
        int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll")] static extern IntPtr GetTopWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint f);
    [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string? n);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern bool Shell_NotifyIcon(uint msg, ref NOTIFYICONDATAW data);

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Size = 976)]
    struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    const int GWL_EXSTYLE = -20, GWLP_HWNDPARENT = -8;
    const int WS_EX_LAYERED = 0x00080000, WS_EX_TOOLWINDOW = 0x00000080;
    const int WS_POPUP = unchecked((int)0x80000000);
    const uint NIM_ADD = 0, NIM_DELETE = 2;
    const uint NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4;

    public static void Run()
    {
        var sb = new StringBuilder();
        void W(string s) { sb.AppendLine(s); }

        W("===== LegionBatteryTray 诊断报告 =====");
        W("时间        : " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        W("exe         : " + Environment.ProcessPath);
        W("系统        : " + Environment.OSVersion.VersionString + " (build " + Environment.OSVersion.Version.Build + ")");
        W("64位进程    : " + Environment.Is64BitProcess);
        W("会话 ID     : " + System.Diagnostics.Process.GetCurrentProcess().SessionId);
        W("用户交互    : " + Environment.UserInteractive);
        W("完整性级别  : " + TokenInfo.IntegrityLevel());
        W("");

        // ---- 任务栏 ----
        W("----- 任务栏窗口 -----");
        IntPtr tray = FindWindow("Shell_TrayWnd", null);
        W("Shell_TrayWnd      : " + H(tray));
        if (tray != IntPtr.Zero)
        {
            W("  可见             : " + IsWindowVisible(tray));
            if (GetWindowRect(tray, out RECT tr))
                W("  矩形             : (" + tr.Left + "," + tr.Top + ")-(" + tr.Right + "," + tr.Bottom + ")  高 " + (tr.Bottom - tr.Top));
            IntPtr notify = FindWindowEx(tray, IntPtr.Zero, "TrayNotifyWnd", null);
            W("  TrayNotifyWnd    : " + H(notify));
            if (notify != IntPtr.Zero && GetWindowRect(notify, out RECT nr))
                W("  TrayNotify 矩形  : (" + nr.Left + "," + nr.Top + ")-(" + nr.Right + "," + nr.Bottom + ")");
        }
        W("GetShellWindow     : " + H(GetShellWindow()));
        W("前台窗口           : " + H(GetForegroundWindow()));
        W("Z 序最上层窗口     : " + H(GetTopWindow(IntPtr.Zero)));
        W("");

        // ---- 三种贴片路径实测 ----
        W("----- 贴片挂载能力实测 -----");
        IntPtr probe = CreateWindowEx(WS_EX_LAYERED | WS_EX_TOOLWINDOW, "STATIC", "diag",
            WS_POPUP, -2000, -2000, 40, 20, IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
        W("CreateWindowEx     : " + H(probe));
        if (probe != IntPtr.Zero)
        {
            if (tray != IntPtr.Zero)
            {
                IntPtr r = SetParent(probe, tray);
                int err = Marshal.GetLastWin32Error();
                W("SetParent(tray)    : 返回 " + H(r) + "  GetParent=" + H(GetParent(probe))
                  + "  err=" + err + "  => " + (GetParent(probe) == tray ? "可嵌入(embed)" : "被拒绝，只能用 overlay"));
                if (GetParent(probe) == tray) SetParent(probe, IntPtr.Zero);
            }
            else W("SetParent          : 跳过（没有任务栏窗口）");

            if (tray != IntPtr.Zero)
            {
                SetWindowLongPtr(probe, GWLP_HWNDPARENT, tray);
                bool owned = GetWindow(probe, 4 /*GW_OWNER*/) == tray;
                W("设 owner=任务栏    : " + (owned ? "成功" : "失败") + "（成功则该贴片总在任务栏之上）");
                if (owned) SetWindowLongPtr(probe, GWLP_HWNDPARENT, IntPtr.Zero);
            }

            bool top = SetWindowPos(probe, new IntPtr(-1), -2000, -2000, 40, 20, 0x0010 | 0x0004);
            W("SetWindowPos TOPMOST: " + top + " err=" + Marshal.GetLastWin32Error());
            DestroyWindow(probe);
        }
        W("");

        // ---- 托盘图标（用户权限下和沙箱里结果完全不同）----
        W("----- 通知区图标 Shell_NotifyIcon 实测 -----");
        IntPtr host = CreateWindowEx(WS_EX_TOOLWINDOW, "STATIC", "diaghost", WS_POPUP,
            0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
        W("宿主窗口           : " + H(host));
        if (host != IntPtr.Zero)
        {
            var nid = new NOTIFYICONDATAW
            {
                cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
                hWnd = host,
                uID = 1,
                uFlags = NIF_MESSAGE | NIF_TIP,
                uCallbackMessage = 0x0400 + 1,
                szTip = "诊断用图标",
                szInfo = "",
                szInfoTitle = "",
            };
            W("cbSize（应为 976） : " + nid.cbSize);
            bool ok = Shell_NotifyIcon(NIM_ADD, ref nid);
            W("NIM_ADD            : " + ok + "  err=" + Marshal.GetLastWin32Error()
              + "  => " + (ok ? "托盘图标可用（用户权限下没问题）" : "被拒绝，只能走任务栏贴片"));
            if (ok) Shell_NotifyIcon(NIM_DELETE, ref nid);
            DestroyWindow(host);
        }
        W("");

        // ---- 电量 ----
        W("----- 鼠标电量读取 -----");
        W("接收器在不在       : " + Hid.ReceiverPresent);
        var frame = Hid.Read();
        if (frame == null)
        {
            W("结果               : 读不到");
            W("诊断               : " + Hid.LastDiagnostic);
            W("");
            W("说明：标准鼠标通道正常时，若这里仍读不到，说明接收器的厂商状态缓存是空的。");
            W("      这条 33 字节特性通道需要主机端软件完成一次握手才会被填充。");
        }
        else
        {
            W("结果               : 电量 " + frame.Percent + "%  " + (frame.Charging ? "充电中" : "电池供电"));
            W("原始帧             : " + Convert.ToHexString(frame.Raw));
            W("设备路径           : " + frame.Path);
        }
        W("");

        // ---- 现有自启状态 ----
        W("----- 自启状态 -----");
        W("已注册自启         : " + AutoStart.IsEnabled());
        W("生效方式           : " + (AutoStart.CurrentMethod() ?? "无"));
        W("启动文件夹         : " + Environment.GetFolderPath(Environment.SpecialFolder.Startup));

        // 实测写一次再撤销，验证这台机器上自启到底能不能落地
        W("");
        W("----- 自启可写性实测 -----");
        bool wasOn = AutoStart.IsEnabled();
        var setResult = AutoStart.Set(true);
        W("尝试开启           : " + (setResult.Ok ? "成功（" + setResult.Method + "）" : "失败 - " + setResult.Reason));
        W("写后回读           : " + AutoStart.IsEnabled() + "  方式=" + (AutoStart.CurrentMethod() ?? "无"));
        if (!wasOn) AutoStart.Set(false);

        string path = Path.Combine(AppContext.BaseDirectory, "diag.txt");
        try
        {
            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
            W("");
            W("报告已写入: " + path);
        }
        catch (Exception e)
        {
            W("写文件失败: " + e.Message);
        }

        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("BATTERYTRAY_DIAG_CONSOLE")))
            Console.WriteLine(sb.ToString());
    }

    static string H(IntPtr p) => p == IntPtr.Zero ? "0x0" : "0x" + p.ToInt64().ToString("X");
}
