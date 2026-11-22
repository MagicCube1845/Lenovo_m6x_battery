namespace BatteryTray;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // 第一件事：清空上一次的日志，只让本次运行的内容留下。
        // （--once / --diag 这类一次性命令也会清，这样每次结果都是干净的）
        TrayContext.ResetLog();

        // 最外层兜底：Main 里任何位置炸掉都要留下证据，绝不能静默退出
        // （用户端"双击没反应"最可能就是启动早期异常把进程直接杀掉）
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            try
            {
                TrayContext.Log("fatal(unhandled): " + e.ExceptionObject);
                File.AppendAllText(
                    Path.Combine(AppContext.BaseDirectory, "crash.txt"),
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}{e.ExceptionObject}{Environment.NewLine}{Environment.NewLine}");
            }
            catch { }
        };

        try
        {
            Run(args);
        }
        catch (Exception ex)
        {
            try
            {
                TrayContext.Log("fatal(main): " + ex);
                string crash = Path.Combine(AppContext.BaseDirectory, "crash.txt");
                File.AppendAllText(crash,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");

                if (!args.Any(a => a.Equals("--silent", StringComparison.OrdinalIgnoreCase)))
                {
                    MessageBox.Show(
                        "启动失败：" + ex.Message +
                        "\n\n详细信息已写入：" + crash,
                        "鼠标电量", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
            catch { }
        }
    }

    static void Run(string[] args)
    {
        bool once = args.Any(a => a.Equals("--once", StringComparison.OrdinalIgnoreCase));
        bool preview = args.Any(a => a.Equals("--icon", StringComparison.OrdinalIgnoreCase));
        bool diag = args.Any(a => a.Equals("--diag", StringComparison.OrdinalIgnoreCase));
        TrayContext.Silent = args.Any(a => a.Equals("--silent", StringComparison.OrdinalIgnoreCase));

        if (diag)
        {
            Diag.Run();
            return;
        }

        // --interval <秒>：改刷新区间（默认 60 秒；电量变化很慢，没必要更勤）
        int idx = Array.FindIndex(args, a => a.Equals("--interval", StringComparison.OrdinalIgnoreCase));
        if (idx >= 0 && idx + 1 < args.Length && int.TryParse(args[idx + 1], out int sec) && sec >= 5)
            TrayContext.RefreshSeconds = Math.Min(sec, 3600);
        else if (idx >= 0)
            TrayContext.Log("参数: --interval 无效（需要 5..3600 的整数），沿用默认 " + TrayContext.RefreshSeconds + " 秒");

        if (preview)
        {
            WriteStripPreview();
            return;
        }

        // --autostart on|off：不开界面直接设置自启并打印结果（排查用）
        int asIdx = Array.FindIndex(args, a => a.Equals("--autostart", StringComparison.OrdinalIgnoreCase));
        if (asIdx >= 0)
        {
            // 只看 --autostart 后面那一个参数，别把命令行里任意位置的 "on" 当开关
            string mode = asIdx + 1 < args.Length ? args[asIdx + 1].Trim().ToLowerInvariant() : "on";
            bool on = mode switch
            {
                "off" or "0" or "disable" or "false" => false,
                _ => true,
            };
            var r = AutoStart.Set(on);
            string report =
                $"请求: {(on ? "开启" : "关闭")} 自启{Environment.NewLine}" +
                $"结果: {(r.Ok ? "成功" : "失败")}{Environment.NewLine}" +
                $"方式: {r.Method}{Environment.NewLine}" +
                $"原因: {r.Reason}{Environment.NewLine}" +
                $"完整性级别: {TokenInfo.IntegrityLevel()}{Environment.NewLine}" +
                $"当前是否有自启项: {AutoStart.IsEnabled()}{Environment.NewLine}" +
                $"启动文件夹: {Environment.GetFolderPath(Environment.SpecialFolder.Startup)}";
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "autostart.txt"), report);
            TrayContext.Log("autostart probe: " + report.Replace(Environment.NewLine, " | "));
            return;
        }

        if (once)
        {
            var frame = Hid.Read();
            string text = frame == null
                ? "未找到鼠标（VID_17EF PID_61B7）"
                : $"电量 {frame.Percent}%，{(frame.Charging ? "正在充电" : "电池供电")}，原始帧 {Convert.ToHexString(frame.Raw)}";
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "once.txt"), text);
            TrayContext.Log("once: " + text);
            return;
        }

        // 第一件事就留痕：即使后面构造界面时炸掉，也能从日志看出程序确实启动过、
        // 以及它拿到的权限身份（用户端"双击没反应"最需要这个证据）
        try
        {
            TrayContext.Log($"=== 启动 {DateTime.Now:yyyy-MM-dd HH:mm:ss}  pid={Environment.ProcessId} " +
                            $"64位={Environment.Is64BitProcess} 权限={TokenInfo.IntegrityLevel()} " +
                            $"exe={Environment.ProcessPath} 命令行={Environment.CommandLine} " +
                            $"自启项={(AutoStart.CurrentMethod() ?? "无")}");
        }
        catch { }

        // 去掉"来自网络"标记：带这个标记的 exe 在开机自启时会弹
        // "打开文件 - 安全警告 / 无法验证发布者"。启动时顺手清一次，之后就再也不弹。
        try
        {
            if (Environment.ProcessPath is { Length: > 0 } selfPath)
                AutoStart.UnblockFile(selfPath);
        }
        catch { }

        // 单实例控制。
        //
        // 原写法 new Mutex(true, name, out created) 有两个坑：
        //  ① 即使用 created=false（说明已有实例），那个 true 仍会去申请所有权；
        //  ② 上一条命的进程被强杀后，内核对象可能还挂在某些句柄上，
        //     于是新进程永远拿到 created=false，直接 return —— 表现为
        //     "双击没反应 / 复制到别处运行任务栏什么都没有"，而且完全没有提示。
        //
        // 现在：先只做存在性判断；确实被占用才认为已有实例，并且给出可见反馈。
        Mutex? mutex = null;
        bool alreadyRunning;
        try
        {
            mutex = new Mutex(initiallyOwned: false, @"Local\LegionBatteryTray_SingleInstance", out bool createdNew);
            alreadyRunning = !createdNew;
        }
        catch (AbandonedMutexException)
        {
            // 上一个实例是被强杀的，这里接管即可
            alreadyRunning = false;
        }

        // 僵尸互斥体自愈：互斥体存在，但进程列表里其实没有同名的另一个实例。
        // （进程被强杀 / 崩溃后，内核对象可能仍被残留句柄维持，导致新进程永远起不来）
        if (alreadyRunning && !AnotherInstanceAlive())
        {
            TrayContext.Log("互斥锁仍被占用，但没有任何存活的 BatteryTray 进程 → 判定为僵尸，强制接管");
            alreadyRunning = false;
        }

        if (alreadyRunning)
        {
            // 不再静默退出：用户需要知道"已经在跑了"
            TrayContext.Log("已有实例在运行，本次退出");
            if (!TrayContext.Silent)
            {
                MessageBox.Show(
                    "鼠标电量已经在任务栏上显示了，不用重复启动。\n\n" +
                    "如果你在任务栏上找不到它，可能是被折叠进了 ^ 或隐藏了；" +
                    "可以先在任务管理器里结束所有 BatteryTray 进程再重新运行。",
                    "鼠标电量",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
            return;
        }

        // DPI 感知必须在任何窗口创建之前生效，否则 GetWindowRect / SetWindowPos
        // 拿到的都是被虚拟化过的逻辑坐标，贴片会被缩放到屏幕中间。
        // 优先用 Win32 的 SetProcessDpiAwarenessContext（最可靠，per-monitor v2），
        // 失败再退回 WinForms 的 SetHighDpiMode。
        try
        {
            if (!NativeDpi.TryEnablePerMonitorV2())
                Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        }
        catch
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        }
        TrayContext.Log("dpi: " + NativeDpi.Describe());

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // 提权运行是个死路：High 完整性进程既贴不到 explorer（Medium）的任务栏上，
        // 也不该往用户注册表里写自启项。与其让用户看到"有进程但没图标"，不如直接说清楚。
        bool elevated = TokenInfo.IsElevated();
        TrayContext.Log($"权限: {TokenInfo.IntegrityLevel()}  提权={elevated}");
        if (elevated && !TrayContext.Silent)
        {
            MessageBox.Show(
                "这个程序不需要管理员权限，而且用管理员运行时反而不会显示电量。\n\n" +
                "原因：任务栏属于当前用户的资源管理器（普通权限）。管理员进程和它不在同一层，" +
                "窗口既贴不上去，也写不了你的开机启动项。\n\n" +
                "请关掉这个窗口，直接双击 BatteryTray.exe（不要选“以管理员身份运行”）。",
                "鼠标电量 · 请不要用管理员权限运行",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        AppDomain.CurrentDomain.UnhandledException += (_, e) => TrayContext.Log("fatal: " + e.ExceptionObject);
        Application.ThreadException += (_, e) => TrayContext.Log("ui exception: " + e.Exception);

        try
        {
            TrayContext.Log("start");
            Application.Run(new TrayContext());
        }
        catch (Exception ex)
        {
            TrayContext.Log("fatal: " + ex);
            throw;
        }
        finally
        {
            // 这个互斥体是用 initiallyOwned:false 建的 —— 我们从来没有 WaitOne 取得所有权，
            // 所以**不能**调 ReleaseMutex（必定抛 ApplicationException）。
            // 单实例语义靠"内核对象是否存在"实现：进程退出时句柄自动关闭，对象随之消失。
            // 只要把句柄 Dispose 掉就够了。
            try { mutex?.Dispose(); } catch { }
        }
    }

    /// <summary>
    /// 除了自己以外，还有没有正在运行的 BatteryTray 进程？
    /// 用来识别"僵尸互斥体"：互斥体还在，但持有它的进程早就死了。
    ///
    /// 按**可执行文件路径**比对，而不是进程名 —— 用户把 exe 改个名字，
    /// 按名字就永远找不到，于是把真在跑的实例误判成僵尸，导致双开。
    /// </summary>
    static bool AnotherInstanceAlive()
    {
        try
        {
            int me = Environment.ProcessId;
            string? myPath = Environment.ProcessPath;

            // 注意：不能用 GetProcessesByName("BatteryTray") 先按名字过滤——
            // 用户把 exe 改名后进程名跟着变，过滤会直接把真实例筛掉，
            // 然后误判成"僵尸互斥体"强制接管，导致双开。只能全量枚举再按路径比。
            foreach (var p in System.Diagnostics.Process.GetProcesses())
            {
                if (p.Id == me) continue;
                try
                {
                    string? path = p.MainModule?.FileName;
                    if (path == null || myPath == null) continue;
                    if (string.Equals(path, myPath, StringComparison.OrdinalIgnoreCase)) return true;
                }
                catch
                {
                    // 64 位进程读 32 位进程（或反之）的 MainModule 会抛，跳过即可
                }
                finally
                {
                    p.Dispose();
                }
            }
            return false;
        }
        catch
        {
            // 查不到就保守认为有（宁可提示一次，也不要重复开两个贴片）
            return true;
        }
    }

    /// <summary>把四种状态画成一张对比图（浅色/深色任务栏各一行），用于文档与验收。</summary>
    static void WriteStripPreview()
    {
        var dir = AppContext.BaseDirectory;
        var samples = new (int pct, bool charging, bool connected, string caption)[]
        {
            (82, false, true, "82% 电池供电"),
            (100, true, true, "充电中（蓝色）"),
            (14, false, true, "14% 低电量"),
            (-1, false, false, "鼠标未连接"),
        };

        const int scale = 3;
        int trayH = 48 * scale;
        var rendered = samples.Select(s => TaskbarPill.RenderPreview(trayH, s.pct, s.charging, s.connected, light: true)).ToList();

        int pad = 14 * scale;
        int captionH = 18 * scale;
        int width = rendered.Sum(b => b.Width + pad) + pad;
        int height = pad * 2 + (trayH + captionH) * 2;

        using var sheet = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(sheet))
        {
            g.Clear(Color.FromArgb(0xF3, 0xF4, 0xF6));
            using var caption = new Font("Segoe UI", 9f * scale / 2f);
            int x = pad;
            for (int i = 0; i < rendered.Count; i++)
            {
                var b = rendered[i];
                int y = pad + (trayH - b.Height) / 2;
                g.DrawImage(b, x, y, b.Width, b.Height);
                g.DrawString(samples[i].caption, caption, Brushes.DimGray, x, pad + trayH + 6 * scale);
                x += b.Width + pad;
            }

            // 深色任务栏对照
            using var dark = new SolidBrush(Color.FromArgb(0x20, 0x20, 0x20));
            g.FillRectangle(dark, 0, pad * 2 + trayH + captionH, width, trayH + captionH);
            x = pad;
            foreach (var s in samples)
            {
                using var b = TaskbarPill.RenderPreview(trayH, s.pct, s.charging, s.connected, light: false);
                int y = pad * 2 + trayH + captionH + (trayH - b.Height) / 2;
                g.DrawImage(b, x, y, b.Width, b.Height);
                x += b.Width + pad;
            }
        }

        foreach (var b in rendered) b.Dispose();
        sheet.Save(Path.Combine(dir, "strip_preview.png"), System.Drawing.Imaging.ImageFormat.Png);
        TrayContext.Log("strip preview written");
    }
}

