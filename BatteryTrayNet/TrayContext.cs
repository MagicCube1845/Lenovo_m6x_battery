namespace BatteryTray;

/// <summary>
/// 只做一件事：在任务栏（时钟左边）画「鼠标图标 + 电量百分比」。
/// 不建通知区图标、不建任务栏按钮，所以任务栏上不会多出程序图标。
/// </summary>
public sealed class TrayContext : ApplicationContext
{
    public static bool Silent { get; set; }

    /// <summary>电量刷新区间（秒），默认 60，可由 <c>--interval</c> 覆盖。</summary>
    public static int RefreshSeconds { get; set; } = 60;

    readonly TaskbarPill _pill = new();
    readonly System.Windows.Forms.Timer _timer;
    readonly System.Windows.Forms.Timer _watchdog;
    readonly PillMenu _menu = new();

    int _percent = -1;
    bool _charging;
    bool _connected;
    bool _autoStartOn;
    bool _refreshing;      // 防重入：上一轮还没回来就不再派新的
    bool _wasAlive = true; // watchdog 只在状态变化时打日志，避免每 2 秒刷一行
    bool _quitting;        // 退出后到达的延迟回调直接丢弃，不再碰已 Dispose 的贴片

    /// <summary>UI 线程的同步上下文。HID 读取在后台线程完成，结果必须回到这里才能碰窗口。</summary>
    readonly SynchronizationContext? _uiCtx;

    public static string LogPath => Path.Combine(AppContext.BaseDirectory, "BatteryTray.log");

    /// <summary>
    /// 每次启动时清空上一次的日志，只保留本次运行的内容。
    /// 在 Program.Main 最开头调用一次；之后所有 Log() 都是追加。
    /// 失败（比如文件被别的进程占着）时忽略，不影响启动。
    /// </summary>
    public static void ResetLog()
    {
        try
        {
            if (File.Exists(LogPath)) File.Delete(LogPath);
        }
        catch
        {
            try { File.WriteAllText(LogPath, ""); } catch { }
        }
    }

    public static void Log(string message)
    {
        try
        {
            File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss} {message}{Environment.NewLine}");
        }
        catch
        {
            // 日志写不了不影响功能
        }
    }

    public TrayContext()
    {
        // 构造函数在 Application.Run 之前执行，那时 WinForms 往往还没装同步上下文，
        // 先手动装一个，后面后台线程才能把结果安全地投回 UI 线程。
        if (SynchronizationContext.Current is not System.Windows.Forms.WindowsFormsSynchronizationContext)
            SynchronizationContext.SetSynchronizationContext(
                new System.Windows.Forms.WindowsFormsSynchronizationContext());
        _uiCtx = SynchronizationContext.Current;

        _percent = Settings.GetInt("LastPercent", -1);
        _autoStartOn = AutoStart.IsEnabled();

        // 老版本写下的自启项缺少 --silent，顺手升级（失败无所谓，不影响主功能）
        if (_autoStartOn) AutoStart.Repair();

        _pill.LeftClick += () => Refresh();
        _pill.RightClick += () => ShowMenu();

        Refresh();

        Log($"pill: mode={_pill.ModeName}");

        _timer = new System.Windows.Forms.Timer { Interval = Math.Max(5, RefreshSeconds) * 1000 };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();

        // explorer 重启会把任务栏窗口换掉，贴片要跟着重挂；
        // 同时兼作"全屏检测"的节拍器 —— 500ms 足够跟上游戏/视频的全屏切换，
        // 每次只做一次 GetForegroundWindow + 矩形比较，开销可忽略。
        _watchdog = new System.Windows.Forms.Timer { Interval = 500 };
        _watchdog.Tick += (_, _) =>
        {
            _pill.Tick();
            // 只在"挂上/掉下来"的瞬间记一笔，避免每 500ms 刷一行日志
            bool alive = _pill.Alive;
            if (alive != _wasAlive)
            {
                _wasAlive = alive;
                Log(alive ? "pill: 已挂到任务栏" : "pill: 未挂到任务栏（稍后重试）");
            }
        };
        _watchdog.Start();
    }

    // ——— 菜单 ———

    /// <summary>
    /// 弹出右键菜单。
    ///
    /// 用自绘的 PillMenu，而不是 WinForms 的 ContextMenuStrip —— 后者是**无 owner 的顶层窗口**，
    /// 会跑到任务栏上（"凭空多出一个窗口"）；给它补 owner 之后又会连带把贴片一起弄没。
    /// 自绘菜单是 TOOLWINDOW + NOACTIVATE 的分层窗口，天然不上任务栏，外观也能做成圆角卡片。
    /// </summary>
    /// <summary>
    /// 弹出右键菜单。
    ///
    /// **必须去重**：一次右键会从三条路进来 ——
    ///   ① 贴片窗口的 WM_RBUTTONUP
    ///   ② 贴片窗口的 WM_CONTEXTMENU（Windows 紧跟 ① 之后自动补发）
    ///   ③ 贴片的 WH_MOUSE_LL 钩子（贴片被任务栏压住时靠它兜底）
    /// 不去重的话一次右键会连续创建 3 个菜单窗口（实测日志里同一毫秒出现 3 次 Show），
    /// 后面的 Show 会把前面的窗口销毁重建 —— 于是就出现
    /// "点按钮没反应"（点在建/毁的间隙）和"不自动关闭"（关闭钩子被反复卸载重装）。
    /// </summary>
    void ShowMenu()
    {
        // 全屏应用期间贴片是隐藏的，此时不该弹菜单（用户在看视频/玩游戏）
        if (FullScreen.IsActive()) return;

        // 菜单已经开着时，这次右键是"关掉重开"：先关掉旧菜单，直接弹新的，不走防抖。
        // 否则会出现"旧菜单被自动关闭钩子关了、新菜单又被防抖拦住"的空窗——表现为右键没反应。
        if (_menu.IsOpen)
        {
            _menu.Close();
            _lastMenuTick = Environment.TickCount64;
        }
        else
        {
            // 400ms 内的重复请求视为同一次点击
            long now = Environment.TickCount64;
            if (now - _lastMenuTick < 400) return;
            _lastMenuTick = now;
        }

        var items = new List<PillMenu.Entry>
        {
            new(_connected ? $"鼠标电量 {_percent}%" : "鼠标未连接",
                null, false, false, false, null),

            new(_connected
                    ? (_charging ? "充电中（上次电量）" : "电池供电")
                    : "鼠标未连接",
                null, false, false, false, null),

            new("", null, true, false, true, null),

            new("立即刷新", "F5", true, false, false, Refresh),

            new("开机自启", _autoStartOn ? "已开启" : null, true, _autoStartOn, false, ToggleAutoStart),

            new("", null, true, false, true, null),

            new("退出", null, true, false, false, Quit),
        };

        _pill.GetMenuAnchor(out int x, out int y);
        _menu.Show(x, y, items);
        _menu.StartAutoClose(() => _pill.WindowHandle);
    }

    long _lastMenuTick;

    /// <summary>
    /// 勾选/取消「开机自动启动」。
    ///
    /// 全程不弹任何窗口：成功或失败都只写日志和 autostart_result.txt，
    /// 界面上仅"开机自动启动"这一行右侧的「已开启」文字会跟着变。
    /// （之前用气泡提示，实测那个浮窗在某些机器上渲染不出来——只有黑底、还会卡住，
    ///   所以干脆彻底不用任何临时窗口。）
    /// </summary>
    void ToggleAutoStart()
    {
        bool want = !_autoStartOn;
        var result = AutoStart.Set(want);

        Log($"autostart: want={want} ok={result.Ok} method={result.Method} reason={result.Reason}");

        try
        {
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "autostart_result.txt"),
                $"时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}" +
                $"请求: {(want ? "开启" : "关闭")}{Environment.NewLine}" +
                $"结果: {(result.Ok ? "成功" : "失败")}{Environment.NewLine}" +
                $"方式: {result.Method}{Environment.NewLine}" +
                $"当前权限: {TokenInfo.IntegrityLevel()}{Environment.NewLine}" +
                $"回读是否已生效: {AutoStart.IsEnabled()}  ({AutoStart.CurrentMethod() ?? "无"}){Environment.NewLine}" +
                $"详情: {result.Reason}{Environment.NewLine}");
        }
        catch { }

        _autoStartOn = AutoStart.IsEnabled();

        // 不做任何浮窗提示：状态直接体现在菜单该行的「已开启」标记上。
        // 失败细节在 autostart_result.txt / BatteryTray.log 里。
        Apply();
    }

    // ——— 数据 ———

    /// <summary>
    /// 刷新电量。
    ///
    /// **必须在后台线程读 HID**：Hid.ReadOne 的失败路径是 SetFeature→Sleep(100)→GetFeature→Sleep(150)，
    /// 最多 4 次重试、还可能遍历多个候选设备，同步跑在 UI 线程上会卡住几百毫秒到几秒
    /// （贴片、菜单、钩子回调全部停摆）。这里只负责派发，读取结果由 ApplyFrame 回到 UI 线程处理。
    /// </summary>
    void Refresh()
    {
        if (_refreshing) return;   // 上一轮还没回来，跳过本次 tick
        _refreshing = true;

        var ctx = _uiCtx;
        Task.Run(() =>
        {
            BatteryFrame? frame = null;
            string error = "";
            try
            {
                frame = Hid.Read();
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }

            void Done() => ApplyFrame(frame, error);
            if (ctx != null) ctx.Post(_ => Done(), null);
            else Done();   // 兜底：拿不到上下文就当前线程处理（极少发生）
        });
    }

    /// <summary>把一次读取的结果落到状态上并重绘。**只能在 UI 线程调用**。</summary>
    void ApplyFrame(BatteryFrame? frame, string error)
    {
        try
        {
            if (_quitting) return;   // 退出过程中投递进来的结果直接丢弃
            if (error.Length > 0)
            {
                Log("refresh: 读取异常 " + error);
                _connected = false;
                _charging = false;
                Apply();   // 异常也是状态变化，贴片要立刻显示"未连接"，不能继续显示旧电量
                return;
            }

            _connected = frame != null;

            if (frame == null)
            {
                // 断连时充电标志必须一起清掉，否则菜单会同时显示"未连接"和"充电中"
                _charging = false;
                Log("refresh: 未找到鼠标 " + Hid.LastDiagnostic);
            }
            else if (frame.Charging)
            {
                _charging = true;
                Log($"refresh: 充电中（设备报 {frame.Percent}%，忽略）");
            }
            else
            {
                _charging = false;
                _percent = frame.Percent;
                Settings.SetInt("LastPercent", _percent);
                Log($"refresh: 电量 {frame.Percent}% · 电池供电");
            }

            Apply();
        }
        finally
        {
            _refreshing = false;
        }
    }

    void Apply()
    {
        _pill.Update(_connected ? _percent : -1, _charging, _connected);
    }

    void Quit()
    {
        _quitting = true;
        _timer.Stop();
        _watchdog.Stop();
        _menu.Dispose();
        _pill.Dispose();
        Log("exit");
        ExitThread();
    }
}
