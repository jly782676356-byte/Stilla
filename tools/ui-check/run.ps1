# 界面渲染检查：编译测试台、生成假数据、渲染指定场景到 PNG。
#   用法：powershell -ExecutionPolicy Bypass -File tools\ui-check\run.ps1 [-Scenario compact|edge|scale|panel|views|resize|panelscale|toggle|consent|all] [-Out <文件夹>]
# 全程用假数据，设置放在临时文件夹，不会碰真实的 Stilla 设置，也不会弹出任何窗口。
param(
    [string]$Scenario = "all",
    [string]$Out = ""
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path))
if (-not $Out) { $Out = Join-Path $root "tools\ui-check\out" }
$work = Join-Path $env:TEMP "stilla-ui-check"
Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $work | Out-Null

$csc = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$src = Join-Path $root "src"
$sources = @("Main.cs", "Source.cs", "Shared.cs", "Update.cs", "Settings.cs", "Skins.cs", "Skins2.cs", "Accounts.cs") | ForEach-Object { Join-Path $src $_ }
$sources += (Join-Path $root "tools\ui-check\Harness.cs")
$exe = Join-Path $work "Harness.exe"
$args1 = @("/nologo", "/codepage:65001", "/target:exe", "/main:Stilla.Harness", "/out:$exe",
    "/r:System.Windows.Forms.dll", "/r:System.Drawing.dll", "/r:System.Web.Extensions.dll") + $sources
& $csc $args1
if ($LASTEXITCODE -ne 0) { throw "Harness 编译失败" }

python (Join-Path $root "tools\make_demo_data.py") $work | Out-Null
$env:STILLA_DEMO = "1"
$env:STILLA_ISOLATED = "1"       # 只读假数据目录，绝不碰真实的 ~\.claude（否则截图里会出现真实会话标题）
$env:STILLA_HOME = Join-Path $work "home"
$env:CLAUDE_CONFIG_DIR = Join-Path $work "claude"
New-Item -ItemType Directory -Force $Out | Out-Null
$list = if ($Scenario -eq "all") { @("compact", "edge", "scale", "panel", "views", "resize", "panelscale", "toggle", "consent") } else { @($Scenario) }
foreach ($sc in $list) {
    & $exe $Out $sc
    if ($LASTEXITCODE -ne 0) { throw "场景 $sc 失败" }
}
Remove-Item Env:\STILLA_DEMO, Env:\STILLA_ISOLATED, Env:\STILLA_HOME, Env:\CLAUDE_CONFIG_DIR -ErrorAction SilentlyContinue
