# 本地打包脚本：产出与 GitHub Actions 完全一致的两个发布包
#   .\build-release.ps1                 # 打两个包
#   .\build-release.ps1 -Version 1.0.1  # 指定版本
#   .\build-release.ps1 -StandaloneOnly # 只打免安装版（需联网下载运行时包）
#
# 产物在 publish\ 下：
#   BatteryTray-standalone-win-x64.zip            免安装，双击即用
#   BatteryTray-framework-dependent-win-x64.zip   需 .NET 10 Desktop Runtime
param(
    [string]$Version = '1.0.0',
    [switch]$StandaloneOnly,
    [switch]$FrameworkOnly
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$proj = Join-Path $root 'BatteryTrayNet\BatteryTray.csproj'
$publishRoot = Join-Path $root 'publish'

if (-not (Test-Path $proj)) { throw "找不到项目文件: $proj" }

# 沙箱里默认的 $HOME\.dotnet 不可写，统一指到工作区内
$env:DOTNET_CLI_HOME = Join-Path $root 'dotnet-home'
$env:APPDATA = Join-Path $root 'dotnet-home\appdata'
$env:NUGET_PACKAGES = Join-Path $root 'dotnet-home\packages'
$env:DOTNET_NOLOGO = '1'

$variants = @()
if (-not $FrameworkOnly) {
    $variants += @{ Name = 'standalone'; SelfContained = 'true'; SingleFile = 'true'; Zip = 'BatteryTray-standalone-win-x64' }
}
if (-not $StandaloneOnly) {
    $variants += @{ Name = 'framework-dependent'; SelfContained = 'false'; SingleFile = 'false'; Zip = 'BatteryTray-framework-dependent-win-x64' }
}

foreach ($v in $variants) {
    $out = Join-Path $publishRoot $v.Name
    Write-Host "==> 构建 $($v.Name) (v$Version)" -ForegroundColor Cyan
    if (Test-Path $out) { Remove-Item $out -Recurse -Force }

    $dotnetArgs = @(
        'publish', $proj,
        '-c', 'Release',
        '-r', 'win-x64',
        "-p:Version=$Version",
        "-p:FileVersion=$Version.0",
        "-p:AssemblyVersion=$Version.0",
        "-p:SelfContained=$($v.SelfContained)",
        "-p:PublishSingleFile=$($v.SingleFile)",
        "-p:IncludeNativeLibrariesForSelfExtract=$($v.SingleFile)",
        "-p:EnableCompressionInSingleFile=$($v.SingleFile)",
        '-p:DebugType=none',
        '-o', $out
    )
    Write-Host "dotnet $($dotnetArgs -join ' ')"
    dotnet @dotnetArgs
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败（$($v.Name)），退出码 $LASTEXITCODE" }

    $exe = Join-Path $out 'BatteryTray.exe'
    if (-not (Test-Path $exe)) { throw "产物缺少 BatteryTray.exe: $out" }

    if ($v.SelfContained -eq 'true') {
        $extra = Get-ChildItem $out -File | Where-Object { $_.Name -notin @('BatteryTray.exe', 'BatteryTray.pdb') }
        if ($extra) { throw "自包含单文件版不应包含额外文件: $($extra.Name -join ', ')" }
        $notes = @(
            "BatteryTray v$Version - 免安装版 (self-contained, win-x64)",
            '',
            '用法：解压后双击 BatteryTray.exe，无需安装任何运行时。',
            '开机自启：右键任务栏贴片 -> 开机自动启动。',
            '注意：请勿以管理员身份运行，普通权限即可。'
        )
    } else {
        $notes = @(
            "BatteryTray v$Version - 需要 .NET 10 (framework-dependent, win-x64)",
            '',
            '前置条件：安装 .NET 10 Desktop Runtime（不是 ASP.NET Core Runtime）',
            '  https://dotnet.microsoft.com/download/dotnet/10.0',
            '用法：解压后双击 BatteryTray.exe。',
            '注意：请勿以管理员身份运行，普通权限即可。'
        )
    }
    $notes -join "`r`n" | Set-Content -Path (Join-Path $out '使用说明.txt') -Encoding UTF8

    $zip = Join-Path $publishRoot "$($v.Zip).zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path (Join-Path $out '*') -DestinationPath $zip -Force
    Write-Host ("    包: {0} ({1} MB)" -f $zip, [math]::Round((Get-Item $zip).Length / 1MB, 1)) -ForegroundColor Green
}

Write-Host ''
Write-Host '全部完成。产物在 publish\ 目录。' -ForegroundColor Green
Write-Host '发布到 GitHub：' -ForegroundColor Yellow
Write-Host "    git tag v$Version; git push origin v$Version"
