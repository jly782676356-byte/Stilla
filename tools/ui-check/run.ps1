# 界面渲染检查：编译测试台、生成假数据、渲染指定场景到 PNG。
#   用法：powershell -ExecutionPolicy Bypass -File tools\ui-check\run.ps1 [-Scenario <场景>] [-Out <文件夹>] [-Work <文件夹>]
#   旧场景：compact | edge | scale | panel | views | resize | panelscale | toggle | consent | hero | badge | settings | all
#   0.2.0 版面检查场景（QaScenarios.cs / QaDialogs.cs，除了 PNG 还会写 issues_<场景>.txt：被截断 / 重叠 / 贴边的文字）：
#     q_default q_simple q_views q_every q_sizes q_scale q_edge q_tips q_dialogs，或 q_all（全部）
#   例：run.ps1 -Scenario q_default -Out C:\temp\out -Work C:\temp\work
# 全程用假数据，设置放在临时文件夹，不会碰真实的 Stilla 设置，也不会弹出任何（看得见的）窗口。
# 中文树和英文树都能跑：文件名和价格表里的词按 src 里的源码自动认。
param(
    [string]$Scenario = "all",
    [string]$Out = "",
    [string]$Work = "",     # 编译和假数据的工作目录；几个人同时跑时各给一个，免得互相覆盖
    [string]$Src = ""       # 源码文件夹（默认 <根>\src）；别人改到一半编译不过时，可以拷一份快照来跑
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path))
if (-not $Out) { $Out = Join-Path $root "tools\ui-check\out" }
$work = if ($Work) { $Work } else { Join-Path $env:TEMP "stilla-ui-check" }
Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $work | Out-Null

$csc = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$src = if ($Src) { $Src } else { Join-Path $root "src" }
$sources = @("Main.cs", "Source.cs", "Shared.cs", "Update.cs", "Settings.cs", "Skins.cs", "Skins2.cs", "Accounts.cs", "Details.cs", "Extras.cs", "Features.cs", "Jump.cs", "Mail.cs", "Report.cs", "Tips.cs", "WidgetExtra.cs") | ForEach-Object { Join-Path $src $_ }
# 测试台的所有 .cs（Harness.cs、UiProbe.cs、QaScenarios.cs、QaDialogs.cs）
$sources += @("Harness.cs", "UiProbe.cs", "QaScenarios.cs", "QaDialogs.cs") | ForEach-Object { Join-Path $root ("tools\ui-check\" + $_) }   # 别的 .cs（例如 LogicTests.cs）是另一套测试，不在这里编译
$exe = Join-Path $work "Harness.exe"
$args1 = @("/nologo", "/codepage:65001", "/target:exe", "/main:Stilla.Harness", "/define:UICHECK", "/out:$exe",
    "/r:System.Windows.Forms.dll", "/r:System.Drawing.dll", "/r:System.Web.Extensions.dll", "/r:System.Security.dll") + $sources
& $csc $args1
if ($LASTEXITCODE -ne 0) { throw "Harness 编译失败" }

python -I -X utf8 (Join-Path $root "tools\make_demo_data.py") $work $src --rich | Out-Null
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
