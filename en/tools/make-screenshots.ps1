# Re-renders the README screenshots (docs\screenshots\*.png) from fake data. No window pops up, your real settings are never touched.
#   Usage: powershell -ExecutionPolicy Bypass -File tools\make-screenshots.ps1
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$tmp = Join-Path $env:TEMP "stilla-shots"
Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
$run = Join-Path $root "tools\ui-check\run.ps1"
foreach ($sc in "compact", "hero", "settings") {
    & powershell -ExecutionPolicy Bypass -File $run -Scenario $sc -Out $tmp
    if ($LASTEXITCODE -ne 0) { throw "Scenario $sc failed" }
}
$dst = Join-Path $root "docs\screenshots"
New-Item -ItemType Directory -Force $dst | Out-Null
Copy-Item (Join-Path $tmp "compact_light.png") (Join-Path $dst "skins-light.png") -Force
Copy-Item (Join-Path $tmp "compact_dark.png") (Join-Path $dst "skins-dark.png") -Force
Copy-Item (Join-Path $tmp "hero.png") (Join-Path $dst "hero.png") -Force
Copy-Item (Join-Path $tmp "settings.png") (Join-Path $dst "settings.png") -Force
Write-Host "Screenshots written to $dst"
