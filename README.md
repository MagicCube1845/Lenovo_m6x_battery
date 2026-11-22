# Lenovo_m6x_battery

**给联想 Legion M6X / M6X Pro 无线鼠标做的电量读取与任务栏显示工具：不装 Lenovo 全家桶、不开后台服务，
直接读 HID 特性报告拿电量，并把百分比贴在 Windows 11 任务栏上。**

![任务栏效果](BatteryTray/strip_shot.png)

> 📌 **发布描述（Repo description，可直接粘进 GitHub）**
> `Read Lenovo Legion M6X/M6X Pro (VID_17EF PID_61B7) mouse battery over raw HID in C#, and show it as a tiny pill on the Windows 11 taskbar — no vendor software, no background service. 免装联想驱动，直接用 C# 读 Legion M6X Pro 鼠标电量并贴在任务栏。`

---

## 这是什么

联想只在 **LEGION EDGE**（`C:\Program Files (x86)\LEGION EDGE`）里显示自家鼠标电量，
而且对 M6X 系列连电量数字都不给。这个仓库做了两件事：

1. **逆向出读数协议** —— M6X Pro 走的是「33 字节 HID 特性报告 + `[18]` 电量 + `[19]` 充电标志」，
   关键时序是 `SetFeature(0C 01 20 …) → Sleep(100) → GetFeature`，**少了 SetFeature 永远读到 33 个 0x00**。
   协议细节、LEGION EDGE 的两条读取路径（SDK `LNV_GetBattery` / 自研 Dangle 协议）见 [电量获取方法.md](电量获取方法.md)。
2. **把它做成一个能天天用的托盘程序** —— 任务栏上一个小贴片显示 `🖱 82%`，
   颜色随电量变化，充电时变蓝，全屏时自动隐藏，支持开机自启。

## 特性

| | |
|---|---|
| **零依赖** | .NET 10 + WinForms，无第三方 NuGet 包，不依赖 LEGION EDGE 常驻（已实测卸载后仍可读） |
| **无需管理员** | 普通用户权限读 HID，不弹 UAC |
| **任务栏贴片** | `SetParent` 嵌入 `Shell_TrayWnd`，失败自动退化为 `WS_EX_TOPMOST` 分层窗口；explorer 重启自动重挂 |
| **状态颜色** | 绿 ≥60% / 橙 25–59% / 红 <25% / 蓝+闪电=充电 / 灰 `--`=未连接 |
| **轻量** | 实测单核 CPU ≈0.11%，私有内存 ≈11 MB，无 GDI/句柄泄漏 |
| **可排查** | 内置 `--once` / `--diag` / `--probe` 等命令行，每次都写日志 |

## 快速开始

```powershell
# 直接跑现成产物（需要 .NET 10 Desktop Runtime）
.\BatteryTray\BatteryTray.exe

# 只想读一次电量看数字
.\BatteryTray\BatteryTray.exe --once      # 结果写 once.txt 与 BatteryTray.log

# 自己编译
cd BatteryTrayNet
dotnet build -c Release
```

常用参数：`--interval <秒>` 改刷新区间（5–3600，默认 60）、`--autostart on|off` 设置开机自启、
`--silent` 不弹任何提示框。完整说明见 [BatteryTrayNet/README.md](BatteryTrayNet/README.md)。

## 目录结构

```
Lenovo_m6x_battery/
├── BatteryTrayNet/          # 主程序源码（.NET 10 / WinForms）
│   ├── Hid.cs               # ★ 读电量：SetFeature → Sleep(100) → GetFeature，取 [18]/[19]
│   ├── TaskbarPill.cs       # 任务栏贴片：分层窗口 + UpdateLayeredWindow + Z 序看门狗
│   ├── TrayContext.cs       # 托盘/菜单/刷新的宿主与看门狗
│   ├── PillMenu.cs          # 右键菜单（无窗口，不进 Alt+Tab）
│   ├── Storage.cs           # 配置持久化、开机自启、完整性级别
│   ├── FullScreen.cs        # 全屏检测 → 自动隐藏贴片
│   ├── BatteryIcon.cs       # 按状态生成图标（含多 DPI 尺寸）
│   ├── Diag.cs / WindowTree.cs / NativeDpi.cs
│   └── README.md            # 详细的实现笔记与踩坑记录
├── BatteryTray/             # 已编译的可直接运行产物 + 效果截图
├── tools/                   # 4 个 C# 探针工程（托盘/嵌入/全屏/端到端）
└── 电量获取方法.md           # LEGION EDGE 逆向分析：两条协议路径、报文格式、实测数据
```

## 协议速查

设备 `VID_17EF PID_61B7`，HID 集合 `mi_01&col04`（UsagePage `0xFF00`，FeatureReport 33 字节）：

```
请求: SetFeature [0]=0x0C [1]=0x01 [2]=0x20 其余 0        ← 必须发，否则读回全 0
等待: Sleep(100)
响应: GetFeature → 0C 01 20 00 00 01 10 4D 36 30 33 30 ...
                                    [18] [19]
[18] = 电量百分比（0–100）      [19] = 0x01 充电中 / 0x00 电池供电
[7..11] = ASCII "M6030"         用作帧合法性校验
```

⚠️ **充电时设备会把 `[18]` 直接报成 100（假值）**，程序会沿用拔线前最后一次读数并显示为蓝色。

## 说明

* 只针对 **Legion M6X / M6X Pro 2.4G 接收器**实测；其它型号（M3W / M5B / M5 Pro / M7 走联想 SDK 的 520 字节协议）
  协议不同，见 [电量获取方法.md](电量获取方法.md) 路径 A。
* 仓库里的二进制是框架依赖发布，**目标机器需要 .NET 10 Desktop Runtime**；
  想「双击即用」可用 `dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true` 自包含发布。
* 逆向结论基于 LEGION EDGE v1.0.5.0，固件升级后报文偏移可能变化。

## 更新日志

* **v1.0** —— 首次提交：HID 读数、任务栏贴片、右键菜单、开机自启、全屏自动隐藏、诊断命令。
