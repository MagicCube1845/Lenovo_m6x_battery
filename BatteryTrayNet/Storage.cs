using Microsoft.Win32;

namespace BatteryTray;

public static class Settings
{
    const string Key = @"Software\LegionBatteryTray";

    public static int GetInt(string name, int fallback)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(Key);
            return key?.GetValue(name) is int i ? i : fallback;
        }
        catch
        {
            return fallback;   // 注册表不可读时用默认值，不要让启动流程炸掉
        }
    }

    public static bool GetFlag(string name) => GetInt(name, 0) != 0;

    public static void SetInt(string name, int value)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(Key);
            key.SetValue(name, value, RegistryValueKind.DWord);
        }
        catch
        {
            // 忽略：受限令牌下写不进去也不影响主功能
        }
    }
}

/// <summary>一次自启设置的结果：成功时 Method 说明靠什么生效，失败时 Reason 说明为什么。</summary>
public sealed record AutoStartResult(bool Ok, string Method, string? Reason)
{
    public override string ToString() => Ok ? Method : (Reason ?? "未知原因");
}

public static class AutoStart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "LegionBatteryTray";
    const string StartupFileName = "LegionBatteryTray.lnk";

    /// <summary>
    /// 在启动文件夹里创建一个指向 exe 的快捷方式。
    /// 用迟绑定 COM（WScript.Shell）而不是引用 Interop 程序集，避免多一个依赖。
    /// </summary>
    static bool CreateShortcut(string linkPath, string targetExe)
    {
        try
        {
            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return false;

            object? shell = Activator.CreateInstance(shellType);
            if (shell == null) return false;

            object? lnk = null;
            try
            {
                dynamic sh = shell;
                lnk = sh.CreateShortcut(linkPath);
                dynamic l = lnk;
                l.TargetPath = targetExe;
                l.Arguments = "--silent";   // 同 Run 键：开机启动不弹任何窗口
                l.WorkingDirectory = Path.GetDirectoryName(targetExe) ?? AppContext.BaseDirectory;
                l.Description = "鼠标电量（任务栏显示）";
                l.WindowStyle = 1;
                l.Save();
                return File.Exists(linkPath);
            }
            finally
            {
                // 两个 COM 对象都要释放，漏掉 lnk 会一直占着这个 .lnk 文件
                if (lnk != null)
                {
                    try { System.Runtime.InteropServices.Marshal.ReleaseComObject(lnk); } catch { }
                }
                System.Runtime.InteropServices.Marshal.ReleaseComObject(shell);
            }
        }
        catch (Exception ex)
        {
            TrayContext.Log("autostart: 创建快捷方式异常 - " + ex.Message);
            return false;
        }
    }

    /// <summary>去掉"来自网络"标记（MOTW）。有它的话开机启动会弹"无法验证发布者"。</summary>
    public static void UnblockFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            File.Delete(path + ":Zone.Identifier");
        }
        catch
        {
            // 删不掉就算了（不是致命问题）
        }
    }

    /// <summary>
    /// 生成一个能自我定位的启动脚本：不写死 exe 路径，而是用脚本所在位置推算，
    /// 这样即使用户把整个文件夹搬走，脚本仍然有效。
    /// </summary>
    static string BuildCmd(string exe)
    {
        // %~dp0 是脚本自身所在目录；exe 与脚本同目录时最稳。
        // 但也允许 exe 已不在同目录（用户只复制了 cmd），此时退回写死路径。
        string exeName = Path.GetFileName(exe);
        return
            "@echo off\r\n" +
            "rem 开机自动启动 BatteryTray（由程序生成）\r\n" +
            "set \"EXE=%~dp0" + exeName + "\"\r\n" +
            "if not exist \"%EXE%\" set \"EXE=" + exe + "\"\r\n" +
            "start \"\" \"%EXE%\"\r\n" +
            "exit\r\n";
    }

    /// <summary>当前"开机自动启动"是否真的生效（三种落点任一存在即算）。</summary>
    public static bool IsEnabled() => CurrentMethod() != null;

    /// <summary>返回实际生效的方式，没有则 null。</summary>
    public static string? CurrentMethod()
    {
        if (ReadRunValue() != null) return "注册表 Run 键";
        string? cmd = StartupPath();
        if (cmd != null && File.Exists(cmd)) return "启动文件夹";
        return null;
    }

    static string? ReadRunValue()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) as string;
        }
        catch
        {
            return null;
        }
    }

    static string? StartupPath()
    {
        string dir = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        return string.IsNullOrEmpty(dir) ? null : Path.Combine(dir, StartupFileName);
    }

    /// <summary>
    /// 打开/关闭自启。依次尝试：① HKCU Run（正常权限下的标准做法）
    /// ② 用户「启动」文件夹里的 .cmd（Run 写不进去时的落点）。
    /// 每一步都回读验证，不靠"没抛异常"当成功。
    /// </summary>
    public static AutoStartResult Set(bool enabled)
    {
        try
        {
            if (!enabled)
            {
                string? firstError = null;
                try
                {
                    using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
                    key?.DeleteValue(ValueName, false);
                    using var approved = Registry.CurrentUser.OpenSubKey(
                        @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run", writable: true);
                    approved?.DeleteValue(ValueName, false);
                }
                catch (Exception ex) { firstError = ex.Message; }

                try
                {
                    string? link = StartupPath();
                    if (link != null && File.Exists(link)) File.Delete(link);
                }
                catch (Exception ex) { firstError ??= ex.Message; }

                // 顺手清掉老版本留下的 .cmd（它就是开机弹"无法验证发布者"的根源）
                try
                {
                    string? dir = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
                    if (!string.IsNullOrEmpty(dir))
                    {
                        foreach (var legacy in new[] { "LegionBatteryTray.cmd", "LegionBatteryTray.bat", "LegionBatteryTray.vbs" })
                        {
                            string p = Path.Combine(dir, legacy);
                            if (File.Exists(p)) File.Delete(p);
                        }
                    }
                }
                catch { /* 不是致命问题 */ }

                Settings.SetInt("AutoStartWanted", 0);
                return CurrentMethod() == null
                    ? new AutoStartResult(true, "已关闭", null)
                    : new AutoStartResult(false, "", "仍能检测到自启项：" + (firstError ?? "请手动检查 Run 键与启动文件夹"));
            }

            Settings.SetInt("AutoStartWanted", 1);
            string exe = Environment.ProcessPath ?? "";
            if (exe.Length == 0) return new AutoStartResult(false, "", "拿不到自身路径（ProcessPath 为空）");

            // ① 注册表 Run（同时写 StartupApproved，避免被 Win11 当成"已禁用"）
            string? runError = null;
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true))
                    // --silent：开机启动万一在早期崩溃，不要弹出任何消息框（用户要求零弹窗）
                    key.SetValue(ValueName, $"\"{exe}\" --silent");
                using (var key = Registry.CurrentUser.CreateSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run", writable: true))
                    key.SetValue(ValueName, new byte[] { 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, RegistryValueKind.Binary);

                // 回读验证：写进去的值必须真的是我们
                if (ReadRunValue() != null) return new AutoStartResult(true, "注册表 Run 键", null);
                runError = "写入后回读为空";
            }
            catch (Exception ex)
            {
                runError = ex.Message;
                TrayContext.Log("autostart: Run 键写入失败 - " + ex.Message);
            }

            // ② 「启动」文件夹里放一个 .lnk 快捷方式（不是 .cmd）
            //    .cmd 是批处理脚本，一旦文件带"来自网络"标记，开机必然弹
            //    "打开文件 - 安全警告 / 无法验证发布者"。快捷方式不会。
            string? folderError = null;
            try
            {
                string? link = StartupPath();
                if (link == null) folderError = "拿不到启动文件夹路径";
                else if (!Directory.Exists(Path.GetDirectoryName(link))) folderError = "启动文件夹不存在";
                else
                {
                    if (CreateShortcut(link, exe))
                    {
                        UnblockFile(link);
                        return new AutoStartResult(true, "启动文件夹（快捷方式）", null);
                    }
                    folderError = "创建快捷方式失败";
                }
            }
            catch (Exception ex)
            {
                folderError = ex.Message;
                TrayContext.Log("autostart: 启动文件夹写入失败 - " + ex.Message);
            }

            // ③ 两种自动方式都不行：在程序目录生成一个快捷方式，并代开「启动」文件夹，
            //    让用户拖一下即可。注意生成的是 .lnk（不是 .cmd）——
            //    .cmd 一旦带"来自网络"标记，开机必弹"无法验证发布者"。
            string manualLink = Path.Combine(AppContext.BaseDirectory, StartupFileName);
            try
            {
                CreateShortcut(manualLink, exe);
                UnblockFile(manualLink);
                UnblockFile(exe);
            }
            catch { /* 连这个也写不了就算了 */ }

            try
            {
                string? dir = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = dir,
                        UseShellExecute = true,
                    });
            }
            catch { /* 打不开也没关系，下面会给出路径 */ }

            string level = TokenInfo.IntegrityLevel();
            string startupDir = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
            string reason =
                "自动写入没成功，可以手动放一次（只需一次）：\r\n\r\n" +
                $"1) 我已经帮你把快捷方式生成好了：\r\n   {manualLink}\r\n\r\n" +
                $"2) 我已经尝试打开「启动」文件夹；没弹出的话，按 Win+R 输入：\r\n   shell:startup\r\n   位置是：{startupDir}\r\n\r\n" +
                "3) 把上面那个 LegionBatteryTray.lnk 复制进「启动」文件夹，就完成了。\r\n" +
                "   （是快捷方式，不是 .cmd，所以开机不会再弹“无法验证发布者”）\r\n\r\n" +
                $"技术细节：\r\n· Run 键：{runError}\r\n· 启动文件夹：{folderError}\r\n· 当前权限：{level}";
            return new AutoStartResult(false, "", reason);
        }
        catch (Exception ex)
        {
            return new AutoStartResult(false, "", ex.Message);
        }
    }

    /// <summary>启动时自检：用户之前打开过自启、但现在找不到任何自启项，就补写一次。</summary>
    public static void SelfHeal()
    {
        if (Settings.GetInt("AutoStartWanted", 0) == 0) return;
        if (IsEnabled()) return;
        var r = Set(true);
        TrayContext.Log($"autostart: 自检补写 -> {(r.Ok ? r.Method : r.Reason)}");
    }

    /// <summary>
    /// 自启项已存在时，把后来新增的参数补上（目前只有 --silent）。
    /// 老版本写进去的 Run 值不带 --silent，开机崩溃时会弹窗，这里顺手升级一次。
    /// </summary>
    public static void Repair()
    {
        try
        {
            string? current = ReadRunValue();
            if (current == null) return;
            if (current.Contains("--silent", StringComparison.OrdinalIgnoreCase)) return;

            string exe = Environment.ProcessPath ?? "";
            if (exe.Length == 0) return;

            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key == null) return;
            key.SetValue(ValueName, $"\"{exe}\" --silent");
            TrayContext.Log("autostart: 已给现有自启项补上 --silent");
        }
        catch (Exception ex)
        {
            TrayContext.Log("autostart: 补写自启参数失败 " + ex.Message);
        }
    }
}

public static class TokenInfo
{
    // TokenIntegrityLevel 返回的是 TOKEN_MANDATORY_LABEL（SID_AND_ATTRIBUTES + 指向 SID 的指针），
    // 不是 int。之前读成 out int 必然失败，所以永远显示"未知"。
    [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)]
    static extern bool GetTokenInformation(IntPtr token, int cls, IntPtr info, int len, out int ret);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    static extern IntPtr GetCurrentProcess();

    [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)]
    static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr handle);

    [System.Runtime.InteropServices.DllImport("advapi32.dll")]
    static extern int GetSidSubAuthorityCount(IntPtr sid);

    [System.Runtime.InteropServices.DllImport("advapi32.dll")]
    static extern IntPtr GetSidSubAuthority(IntPtr sid, int index);

    const int TokenIntegrityLevel = 25;
    const uint TOKEN_QUERY = 0x0008;

    /// <summary>读当前进程的完整性级别（Low/Medium/High…），用于解释"为什么写不进注册表"。</summary>
    public static string IntegrityLevel()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            // Token 里的完整性级别就是 SID S-1-16-<rid>，托管层直接能拿到，不必碰原始指针
            foreach (var claim in identity.Claims)
            {
                if (!claim.Value.StartsWith("S-1-16-", StringComparison.Ordinal)) continue;
                return claim.Value switch
                {
                    "S-1-16-0" => "Untrusted",
                    "S-1-16-4096" => "Low（受限启动，写不进 Run 键和启动文件夹）",
                    "S-1-16-8192" => "Medium（普通用户权限）",
                    "S-1-16-8448" => "MediumPlus",
                    "S-1-16-12288" => "High（已提权，无法贴到任务栏）",
                    "S-1-16-16384" => "System",
                    _ => claim.Value,
                };
            }
        }
        catch
        {
            // 落到 Win32
        }

        // Win32 兜底：走 SID 指针，全程 try/catch（读错指针会 AV，必须防）
        try
        {
            int rid = ReadIntegrityRid();
            return rid switch
            {
                0x0000 => "Untrusted",
                0x1000 => "Low（受限启动，写不进 Run 键和启动文件夹）",
                0x2000 => "Medium（普通用户权限）",
                0x2100 => "MediumPlus",
                0x3000 => "High（已提权，无法贴到任务栏）",
                0x4000 => "System",
                _ => $"0x{rid:X}",
            };
        }
        catch
        {
            return "未知";
        }
    }

    static int ReadIntegrityRid()
    {
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, out IntPtr token)) return -1;
        IntPtr buffer = IntPtr.Zero;
        try
        {
            GetTokenInformation(token, TokenIntegrityLevel, IntPtr.Zero, 0, out int needed);
            if (needed <= 0) return -1;

            buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal(needed);
            if (!GetTokenInformation(token, TokenIntegrityLevel, buffer, needed, out _)) return -1;

            // TOKEN_MANDATORY_LABEL { SID_AND_ATTRIBUTES { PSID Sid; DWORD Attributes; }; }
            // 注意：Sid 是**指向缓冲区内部**的指针，必须用 IntPtr.Size 读
            IntPtr sid = System.Runtime.InteropServices.Marshal.ReadIntPtr(buffer);
            if (sid == IntPtr.Zero) return -1;

            // SID 布局：Revision(1) SubAuthorityCount(1) IdentifierAuthority(6) SubAuthority[](4*n)
            byte count = System.Runtime.InteropServices.Marshal.ReadByte(sid, 1);
            if (count == 0) return -1;

            // 最后一个子授权就在 SID 起始 + 8 + 4*(count-1)
            int offset = 8 + 4 * (count - 1);
            return System.Runtime.InteropServices.Marshal.ReadInt32(sid, offset);
        }
        finally
        {
            if (buffer != IntPtr.Zero) System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer);
            CloseHandle(token);
        }
    }

    /// <summary>当前进程是否已提权（High/System）。提权后无法把窗口贴到 explorer 的任务栏上。</summary>
    public static bool IsElevated()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }
}

public static class Self
{
    /// <summary>
    /// Windows 11 把通知区每个图标的显示状态记在
    /// HKCU\Control Panel\NotifyIconSettings\&lt;hash&gt;（含 ExecutablePath、IsPromoted）。
    /// 把本程序的 IsPromoted 置 1，就能让它从 ^ 溢出区移到任务栏上。
    /// </summary>
    public static string Promote()
    {
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings", writable: true);
            if (root == null) return "no NotifyIconSettings";

            string? self = Environment.ProcessPath;
            if (string.IsNullOrEmpty(self)) return "no ProcessPath";

            foreach (var name in root.GetSubKeyNames())
            {
                using var sub = root.OpenSubKey(name, writable: true);
                if (sub?.GetValue("ExecutablePath") is not string path) continue;
                if (!string.Equals(path, self, StringComparison.OrdinalIgnoreCase)) continue;

                sub.SetValue("IsPromoted", 1, RegistryValueKind.DWord);
                return "promoted " + name;
            }
            return "no entry for " + Path.GetFileName(self) + " (explorer 尚未为它建条目)";
        }
        catch (Exception ex)
        {
            return "error: " + ex.Message;
        }
    }
}


