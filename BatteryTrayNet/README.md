# Legion 鼠标电量（任务栏小方块）

在**任务栏**上直接显示 Legion M6X Pro 2.4G 鼠标的电量，例如贴近 `^` 的那个绿色 `82%`。
.NET 8（Windows Forms），单进程、无第三方依赖。

![任务栏效果](strip_shot.png)

## 运行

双击 `BatteryTray.exe`。启动后：

* 任务栏右下角（时钟/托盘图标左边）出现一个圆角小方块，写着当前电量百分比；
* 同时会尽力往通知区加一个数字图标（不是必须的，加不上也不影响小方块）；
* 每 60 秒刷新一次，鼠标指针移到小方块上右键可以「立即刷新」。

颜色：**绿 ≥60%**、**橙 25–59%**、**红 <25%**、**蓝 = 正在充电**、**灰 = 鼠标未连接**。

> 为什么不用托盘图标作为唯一出口：Windows 11 默认把新图标塞进 `^` 溢出区，甚至可能被安全软件拒绝。
> 小方块是普通窗口，贴着任务栏画，不受这些限制。

## 命令行

```powershell
.\BatteryTray.exe            # 正常启动（任务栏小方块 + 尽量加托盘图标）
.\BatteryTray.exe --once     # 读一次电量，写 once.txt 并记日志（排查用）
.\BatteryTray.exe --icon     # 生成 icon_preview.png，预览各状态图标
.\BatteryTray.exe --probe    # 只测托盘接口：记录 NIM_ADD 成败/图标是否可见，然后退出
.\BatteryTray.exe --silent   # 不出任何提示框（排查用）
```

## 数据来源

`VID_17EF PID_61B7` 的 HID 集合 `mi_01&col04`（UsagePage `0xFF00`，FeatureReport 33 字节），
Report ID `0x0C`，用 `HidD_GetFeature` 读回：

```
0C 01 20 00 00 01 10 4D 36 30 33 30 01 00 95 25 01 FF 52 00 FF 01 00 01 6C 02 40 38 FF FF B1 16 01
                                              ^^^^ ^^^^
                                              [18] [19]
```

* `[18]` 电量百分比
* `[19]` 充电标志（`01` 充电中 / `00` 电池供电）
* `[7..11]` ASCII `M6030`，用来确认帧合法

**充电时设备会把 `[18]` 直接报成 100**（假值）。程序在充电时用上一次拔线读数显示，并把方块变蓝。
另外这条报告是内部状态快照，偶尔会读到过渡帧；程序校验 `[2]==0x20 && [7..11]=="M6030"`，
不合法就重试，不会显示脏数据。

更深的协议（官方 SDK 的 `LNV_GetBattery`、520 字节特性通道、64 字节邮箱）见上一级 `电量获取方法.md`。

## 日记 / 排查

同目录 `BatteryTray.log` 每次启动追加：

```
18:12:40 start
18:12:40 refresh: 电量 82% · 电池供电
18:12:40 tray: structSize=976 added=False (失败也不影响任务栏小方块)
```

`--probe` 还会记录 `added=`、`rect=`、`promote:` 等托盘接口细节。

## 依赖

需要 **.NET 8 Desktop Runtime**（`Microsoft.WindowsDesktop.App 8.x`）。本机已安装 8.0.x。
若在别的机器上要「双击即用、不装运行时」，用自包含发布：

```powershell
dotnet publish -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

## 自己编译

```powershell
# 系统已安装 .NET 10 SDK（10.0.404）
$env:DOTNET_CLI_HOME='D:\dsh\LEGIONEDGE\dotnet-home'
$env:APPDATA='D:\dsh\LEGIONEDGE\dotnet-home\appdata'      # 沙箱里不能写默认 NuGet 目录
$env:NUGET_PACKAGES='D:\dsh\LEGIONEDGE\dotnet-home\packages'
cd D:\dsh\LEGIONEDGE\BatteryTrayNet
dotnet build -c Release
```
