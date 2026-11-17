# Logic tests for Stilla 0.2.0 (no screenshots, no windows shown, fake data only).
#   usage: powershell -ExecutionPolicy Bypass -File tools\ui-check\run-logic.ps1 [-Work <folder>] [-Filter <group name part>] [-Culture <name>] [-Strict]
#   -Work    compile output + fake data + temp STILLA_HOME folders (default: %TEMP%\stilla-logic-tests; wiped on every run)
#   -Filter  run only the test groups whose name contains this text
#   -Culture run under another regional setting (de-DE, tr-TR, sv-SE, ar-SA ...) to catch culture-dependent parsing / formatting
#   -Strict  count KNOWN-BUG lines as failures too (exit code 1)
# Exit code: 0 = no FAIL lines, 1 = at least one FAIL, 2 = could not compile / start.
# Output is also written (UTF-8) to <Work>\results.txt.
param(
    [string]$Work = "",
    [string]$Filter = "",
    [string]$Culture = "",      # e.g. de-DE, tr-TR, sv-SE: run the whole suite as a user with that regional setting
    [switch]$Strict
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path))
$work = if ($Work) { $Work } else { Join-Path $env:TEMP "stilla-logic-tests" }
$work = [System.IO.Path]::GetFullPath($work)
$marker = Join-Path $work ".stilla-logic-work"

# Safety: only wipe a folder that this script created before (or an empty / missing one). Never wipe anything else.
if (Test-Path $work) {
    $isEmpty = -not (Get-ChildItem -LiteralPath $work -Force | Select-Object -First 1)
    if (-not $isEmpty -and -not (Test-Path $marker)) {
        Write-Host "Refusing to wipe '$work': it is not empty and was not created by run-logic.ps1. Pass an empty or new -Work folder."
        exit 2
    }
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
New-Item -ItemType Directory -Force $work | Out-Null
Set-Content -LiteralPath $marker -Value "stilla logic tests work folder" -Encoding ASCII
# a few source-level checks in the tests read the product sources from here
Set-Content -LiteralPath (Join-Path $work "sources.txt") -Value (Join-Path $root "src") -Encoding UTF8

$csc = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$src = Join-Path $root "src"
$sources = @("Main.cs", "Source.cs", "Shared.cs", "Update.cs", "Settings.cs", "Skins.cs", "Skins2.cs", "Accounts.cs", "Details.cs", "Extras.cs", "Features.cs", "Jump.cs", "Mail.cs", "Report.cs", "Tips.cs", "WidgetExtra.cs") | ForEach-Object { Join-Path $src $_ }
$sources += (Join-Path $root "tools\ui-check\LogicTests.cs")
$exe = Join-Path $work "LogicTests.exe"
$cscArgs = @("/nologo", "/codepage:65001", "/target:exe", "/main:Stilla.LogicTests", "/out:$exe",
    "/r:System.Windows.Forms.dll", "/r:System.Drawing.dll", "/r:System.Web.Extensions.dll", "/r:System.Security.dll") + $sources
& $csc $cscArgs
if ($LASTEXITCODE -ne 0) { Write-Host "compile failed"; exit 2 }

# fake Claude Code data dir (claude\) + fake Stilla home (home\); the tests create their own temp homes under $work
python -I -X utf8 (Join-Path $root "tools\make_demo_data.py") $work | Out-Null

# Everything stays inside $work. The exe sets these again itself and refuses to run if it cannot.
$env:STILLA_DEMO = "1"
$env:STILLA_ISOLATED = "1"
$env:STILLA_NO_UPDATE_CHECK = "1"
$env:STILLA_HOME = Join-Path $work "home"
$env:CLAUDE_CONFIG_DIR = Join-Path $work "claude"

[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
$runArgs = @($work)
if ($Filter) { $runArgs += $Filter }
if ($Culture) { $runArgs += "--culture=$Culture" }
if ($Strict) { $runArgs += "--strict" }
& $exe $runArgs
$code = $LASTEXITCODE
Remove-Item Env:\STILLA_DEMO, Env:\STILLA_ISOLATED, Env:\STILLA_NO_UPDATE_CHECK, Env:\STILLA_HOME, Env:\CLAUDE_CONFIG_DIR -ErrorAction SilentlyContinue
exit $code
