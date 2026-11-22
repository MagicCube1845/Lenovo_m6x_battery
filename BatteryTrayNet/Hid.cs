using System.Runtime.InteropServices;
using System.Text;

namespace BatteryTray;

public sealed record BatteryFrame(int Percent, bool Charging, byte[] Raw, string Path);

/// <summary>
/// 读取 Legion M6X Pro 2.4G（VID_17EF PID_61B7）的电量。
///
/// ★ 协议逆向自 LEGION EDGE 的 NeoUsb 模块（VA 0x591290 那个函数），必须严格按这个时序：
///
///   1. 构造 33 字节请求帧： [0]=0x0C  [1]=0x01  [2]=0x20  其余全 0
///   2. HidD_SetFeature(handle, req, 33)          // 0x21 = 33
///   3. Sleep(100)                                // 汇编里是 push 0x64
///   4. HidD_GetFeature(handle, resp, 33)
///
///   响应帧： [0]=0x0C  [1]=版本  [2]=0x20  [3]=标志字节（实测 0x00 / 0x02 都出现过，勿校验）
///            [7..11]="M6030"  [18]=电量%  [19]=充电标志(1=充电)
///
/// 关键点：**只调 GetFeature 会拿到全 0**。必须先 SetFeature 把请求推下去，
/// 接收器才会把缓存的设备状态填进特性报告。
/// </summary>
public static class Hid
{
    const int DIGCF_PRESENT = 0x02;
    const int DIGCF_DEVICEINTERFACE = 0x10;
    const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2;
    const uint OPEN_EXISTING = 3;
    const uint GENERIC_READ = 0x80000000;
    const uint GENERIC_WRITE = 0x40000000;

    const int TARGET_VID = 0x17EF;
    const int TARGET_PID = 0x61B7;
    const byte REPORT_ID = 0x0C;
    const int FEATURE_LEN = 33;
    const int MAILBOX_LEN = 64;

    /// <summary>SetFeature 之后等待设备应答的时间，沿用 LEGION EDGE 的 100ms。</summary>
    const int SETTLE_MS = 100;

    [StructLayout(LayoutKind.Sequential)]
    struct SP_DEVICE_INTERFACE_DATA
    {
        public uint cbSize;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct HIDD_ATTRIBUTES
    {
        public uint Size;
        public ushort VendorID;
        public ushort ProductID;
        public ushort VersionNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct HIDP_CAPS
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
        public ushort[] Reserved;
    }

    [DllImport("hid.dll")]
    static extern void HidD_GetHidGuid(out Guid guid);

    [DllImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    static extern bool HidD_GetAttributes(IntPtr handle, ref HIDD_ATTRIBUTES attrs);

    [DllImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    static extern bool HidD_GetPreparsedData(IntPtr handle, out IntPtr data);

    [DllImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    static extern bool HidD_FreePreparsedData(IntPtr data);

    [DllImport("hid.dll")]
    static extern int HidP_GetCaps(IntPtr data, ref HIDP_CAPS caps);

    [DllImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    static extern bool HidD_GetFeature(IntPtr handle, byte[] buffer, int length);

    [DllImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    static extern bool HidD_SetFeature(IntPtr handle, byte[] buffer, int length);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SetupDiGetClassDevs(ref Guid guid, IntPtr enumerator, IntPtr hwnd, uint flags);

    [DllImport("setupapi.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfo, ref Guid guid, uint index, ref SP_DEVICE_INTERFACE_DATA data);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.U1)]
    static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, uint detailSize, out uint required, IntPtr devInfo);

    [DllImport("setupapi.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    static extern bool CloseHandle(IntPtr handle);

    /// <summary>最近一次读取的诊断信息，供日志/菜单展示。</summary>
    public static string LastDiagnostic { get; private set; } = "";

    /// <summary>接收器在不在（不管能不能读到电量）。</summary>
    public static bool ReceiverPresent => Enumerate().Count > 0;

    public static BatteryFrame? Read(int retries = 4)
    {
        var candidates = Enumerate();
        if (candidates.Count == 0)
        {
            LastDiagnostic = "未找到鼠标（VID_17EF PID_61B7）";
            return null;
        }

        string last = "";
        foreach (var candidate in candidates)
        {
            var frame = ReadOne(candidate, retries, out last);
            if (frame != null)
            {
                LastDiagnostic = "正常";
                return frame;
            }
        }

        LastDiagnostic = last.Length > 0 ? last : "接收器在线，但没读到有效电量帧";
        return null;
    }

    static List<string> Enumerate()
    {
        var paths = new List<(int score, string path)>();
        HidD_GetHidGuid(out Guid guid);
        IntPtr set = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (set == IntPtr.Zero || set == new IntPtr(-1)) return new List<string>();

        int detailSize = IntPtr.Size == 8 ? 8 : 6;

        try
        {
            for (uint index = 0; ; index++)
            {
                var data = new SP_DEVICE_INTERFACE_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, index, ref data)) break;

                SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out uint needed, IntPtr.Zero);
                if (needed == 0) continue;

                IntPtr buffer = Marshal.AllocHGlobal((int)needed);
                try
                {
                    Marshal.WriteInt32(buffer, detailSize);
                    if (!SetupDiGetDeviceInterfaceDetail(set, ref data, buffer, needed, out _, IntPtr.Zero)) continue;
                    string path = Marshal.PtrToStringUni(IntPtr.Add(buffer, 4)) ?? "";
                    if (path.Length == 0) continue;

                    int score = Probe(path);
                    if (score > 0) paths.Add((score, path));
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }

        return paths.OrderByDescending(p => p.score).Select(p => p.path).ToList();
    }

    /// <summary>0 = 不是目标通道；越高越可信。</summary>
    static int Probe(string path)
    {
        IntPtr handle = CreateFile(path, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (handle == new IntPtr(-1) || handle == IntPtr.Zero) return 0;
        try
        {
            var attrs = new HIDD_ATTRIBUTES { Size = (uint)Marshal.SizeOf<HIDD_ATTRIBUTES>() };
            if (!HidD_GetAttributes(handle, ref attrs)) return 0;

            if (!HidD_GetPreparsedData(handle, out IntPtr preparsed)) return 0;
            HIDP_CAPS caps;
            try
            {
                caps = new HIDP_CAPS { Reserved = new ushort[17] };
                if (HidP_GetCaps(preparsed, ref caps) < 0) return 0;
            }
            finally
            {
                HidD_FreePreparsedData(preparsed);
            }

            // 33 字节特性报告是唯一目标；再按 VID/PID + 厂商 UsagePage 加分
            if (caps.FeatureReportByteLength != FEATURE_LEN) return 0;

            int score = 1;
            if (attrs.VendorID == TARGET_VID && attrs.ProductID == TARGET_PID) score += 4;
            if (caps.UsagePage == 0xFF00 || caps.UsagePage == 0xFF02) score += 2;
            return score;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>
    /// 单次读取：SetFeature 请求 → 等待 → GetFeature 响应。
    /// 时序照抄 LEGION EDGE，偏差会直接导致读到全 0。
    /// </summary>
    static BatteryFrame? ReadOne(string path, int retries, out string diagnostic)
    {
        diagnostic = "";
        IntPtr handle = CreateFile(path, GENERIC_READ | GENERIC_WRITE,
            FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (handle == new IntPtr(-1) || handle == IntPtr.Zero)
        {
            diagnostic = "打不开特性通道（可能被其它程序独占）";
            return null;
        }

        try
        {
            for (int attempt = 0; attempt < retries; attempt++)
            {
                // ① 发请求：0C 01 20
                var request = new byte[FEATURE_LEN];
                request[0] = REPORT_ID;
                request[1] = 0x01;
                request[2] = 0x20;

                if (!HidD_SetFeature(handle, request, request.Length))
                {
                    diagnostic = "HidD_SetFeature 被拒绝（错误码 " + Marshal.GetLastWin32Error() + "）";
                    Thread.Sleep(150);
                    continue;
                }

                // ② 等设备把状态填进特性报告
                Thread.Sleep(SETTLE_MS);

                // ③ 取响应
                var response = new byte[FEATURE_LEN];
                response[0] = REPORT_ID;
                if (!HidD_GetFeature(handle, response, response.Length))
                {
                    diagnostic = "HidD_GetFeature 被拒绝（错误码 " + Marshal.GetLastWin32Error() + "）";
                    Thread.Sleep(150);
                    continue;
                }

                if (IsValid(response))
                    return new BatteryFrame(response[18], response[19] != 0, response, path);

                diagnostic = "收到帧但不是标准状态帧：" + Convert.ToHexString(response);
                Thread.Sleep(150);
            }

            return null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    static bool IsValid(byte[] b)
    {
        if (b.Length < FEATURE_LEN) return false;
        if (b[0] != REPORT_ID) return false;
        // 协议注释里 [2..3] 是 0x2000（小端），但只校验 b[2]。
        // b[3] 不同固件版本可能带别的内容，多校验一位就有把正常帧判死的风险，
        // 而 [0]/[2]/型号串三项已经足够区分"状态帧"和"全 0 空帧"。
        if (b[2] != 0x20) return false;
        return Encoding.ASCII.GetString(b, 7, 5) == "M6030";
    }
}
