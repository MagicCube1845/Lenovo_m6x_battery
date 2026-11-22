using System.Runtime.InteropServices;

namespace BatteryTray;

/// <summary>
/// DPI 感知。贴片是按任务栏的**物理像素**矩形定位的，
/// 如果进程是 DPI-unaware，系统会把坐标按缩放虚拟化，
/// 贴片就会从任务栏跑到屏幕中间（实测 2025,1547 被缩放成 1336,1031）。
/// </summary>
public static class NativeDpi
{
    // SetProcessDpiAwarenessContext 的预定义句柄（Win10 1703+）
    static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new(-4);
    static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE = new(-3);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [DllImport("user32.dll")]
    static extern IntPtr GetThreadDpiAwarenessContext();

    [DllImport("user32.dll")]
    static extern int GetAwarenessFromDpiAwarenessContext(IntPtr value);

    [DllImport("shcore.dll")]
    static extern int SetProcessDpiAwareness(int value);   // 0=unaware 1=system 2=permonitor

    /// <summary>返回是否成功切到 per-monitor v2。</summary>
    public static bool TryEnablePerMonitorV2()
    {
        try
        {
            if (SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2)) return true;
        }
        catch (EntryPointNotFoundException) { }
        catch (DllNotFoundException) { }

        try
        {
            if (SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE)) return true;
        }
        catch { }

        try
        {
            // 2 = PROCESS_PER_MONITOR_DPI_AWARE
            if (SetProcessDpiAwareness(2) == 0) return true;
        }
        catch { }

        return false;
    }

    /// <summary>0=unaware 1=system 2=permonitor。仅供日志。</summary>
    public static string Describe()
    {
        try
        {
            int a = GetAwarenessFromDpiAwarenessContext(GetThreadDpiAwarenessContext());
            return a switch
            {
                0 => "unaware（坐标会被虚拟化！）",
                1 => "system",
                2 => "per-monitor",
                _ => "未知(" + a + ")",
            };
        }
        catch
        {
            return "读取失败";
        }
    }
}
