# UI render check: compiles the harness, generates fake data and renders the chosen scenario to PNGs.
#   Usage: powershell -ExecutionPolicy Bypass -File tools\ui-check\run.ps1 [-Scenario compact|edge|scale|panel|views|resize|panelscale|hero|badge|settings|toggle|consent|all] [-Out <folder>]
# Fake data only, settings live in a temp folder: your real Stilla settings are never touched and no window pops up.
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
if ($LASTEXITCODE -ne 0) { throw "The harness failed to compile" }

python (Join-Path $root "tools\make_demo_data.py") $work | Out-Null
$env:STILLA_DEMO = "1"
$env:STILLA_ISOLATED = "1"       # read only the fake data folder, never the real ~\.claude (otherwise real session titles would show up in screenshots)
$env:STILLA_HOME = Join-Path $work "home"
$env:CLAUDE_CONFIG_DIR = Join-Path $work "claude"
New-Item -ItemType Directory -Force $Out | Out-Null
$list = if ($Scenario -eq "all") { @("compact", "edge", "scale", "panel", "views", "resize", "panelscale", "hero", "badge", "settings", "toggle", "consent") } else { @($Scenario) }
foreach ($sc in $list) {
    & $exe $Out $sc
    if ($LASTEXITCODE -ne 0) { throw "Scenario $sc failed" }
}
Remove-Item Env:\STILLA_DEMO, Env:\STILLA_ISOLATED, Env:\STILLA_HOME, Env:\CLAUDE_CONFIG_DIR -ErrorAction SilentlyContinue
