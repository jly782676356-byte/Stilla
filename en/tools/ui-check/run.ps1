# UI render check: compiles the harness, generates fake data and renders the chosen scenario to PNGs.
#   Usage: powershell -ExecutionPolicy Bypass -File tools\ui-check\run.ps1 [-Scenario <scenario>] [-Out <folder>] [-Work <folder>]
#   Older scenarios: compact | edge | scale | panel | views | resize | panelscale | toggle | consent | hero | badge | settings | all
#   0.2.0 layout-check scenarios (QaScenarios.cs / QaDialogs.cs; besides the PNGs they write issues_<scenario>.txt: clipped / overlapping / border-touching text):
#     q_default q_simple q_views q_every q_sizes q_scale q_edge q_tips q_dialogs, or q_all (all of them)
#   Example: run.ps1 -Scenario q_default -Out C:\temp\out -Work C:\temp\work
# Fake data only, settings live in a temp folder: your real Stilla settings are never touched and no visible window pops up.
# Works for the Chinese and the English tree: file names and words of the price table are taken from the sources in src.
param(
    [string]$Scenario = "all",
    [string]$Out = "",
    [string]$Work = "",     # work folder for the build and the fake data; give each parallel run its own, so they do not overwrite each other
    [string]$Src = ""       # source folder (default <root>\src); copy a snapshot and point here when someone else's half-finished edit breaks the build
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
# every .cs of the harness (Harness.cs, UiProbe.cs, QaScenarios.cs, QaDialogs.cs)
$sources += @("Harness.cs", "UiProbe.cs", "QaScenarios.cs", "QaDialogs.cs") | ForEach-Object { Join-Path $root ("tools\ui-check\" + $_) }   # other .cs files (for example LogicTests.cs in the Chinese tree) are a different test suite and are not compiled here
$exe = Join-Path $work "Harness.exe"
$args1 = @("/nologo", "/codepage:65001", "/target:exe", "/main:Stilla.Harness", "/define:UICHECK", "/out:$exe",
    "/r:System.Windows.Forms.dll", "/r:System.Drawing.dll", "/r:System.Web.Extensions.dll", "/r:System.Security.dll") + $sources
& $csc $args1
if ($LASTEXITCODE -ne 0) { throw "The harness failed to compile" }

python -I -X utf8 (Join-Path $root "tools\make_demo_data.py") $work $src --rich | Out-Null
$env:STILLA_DEMO = "1"
$env:STILLA_ISOLATED = "1"       # read only the fake data folder, never the real ~\.claude (otherwise real session titles would show up in screenshots)
$env:STILLA_HOME = Join-Path $work "home"
$env:CLAUDE_CONFIG_DIR = Join-Path $work "claude"
New-Item -ItemType Directory -Force $Out | Out-Null
$list = if ($Scenario -eq "all") { @("compact", "edge", "scale", "panel", "views", "resize", "panelscale", "toggle", "consent") } else { @($Scenario) }
foreach ($sc in $list) {
    & $exe $Out $sc
    if ($LASTEXITCODE -ne 0) { throw "Scenario $sc failed" }
}
Remove-Item Env:\STILLA_DEMO, Env:\STILLA_ISOLATED, Env:\STILLA_HOME, Env:\CLAUDE_CONFIG_DIR -ErrorAction SilentlyContinue
