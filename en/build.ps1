# Builds Stilla. No Visual Studio / .NET SDK needed: the .NET Framework 4 compiler csc.exe that ships with Windows is enough.
#   Usage: run in this folder   powershell -ExecutionPolicy Bypass -File build.ps1
#   Output (dist\):
#     Stilla.exe                        the floating widget
#     Stilla.Statusline.exe             console helper for Claude Code's status line
#     scripts\                          optional Python scripts
#     Stilla-Setup-<version>-en.exe     single-file installer (everything above is compiled into it)
#     Stilla-<version>-portable-en.zip  portable edition (unzip and run; contains portable.txt so data lives next to the program)
param(
    [string]$Out = "dist"
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$edition = "en"      # UI language of this source tree: zh = Chinese, en = English (used in the installer / zip file names)

$csc = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR "Microsoft.NET\Framework\v4.0.30319\csc.exe" }
if (-not (Test-Path $csc)) { throw "csc.exe (the .NET Framework 4 compiler) was not found" }

# The version number is written in one place only: AppInfo.Version in src\Shared.cs
$sharedText = [IO.File]::ReadAllText((Join-Path $root "src\Shared.cs"), [Text.Encoding]::UTF8)
$version = [regex]::Match($sharedText, 'Version = "([^"]+)"').Groups[1].Value
if (-not $version) { throw "Could not read the version number from src\Shared.cs" }

$dist = Join-Path $root $Out
New-Item -ItemType Directory -Force $dist | Out-Null
$common = @("/nologo", "/codepage:65001", "/optimize+", "/warn:3", "/warnaserror+")

Push-Location (Join-Path $root "src")
try {
    # The widget: a window application (no console)
    $gui = $common + @(
        "/target:winexe", "/win32icon:..\assets\Stilla.ico", "/out:$dist\Stilla.exe",
        "/r:System.Windows.Forms.dll", "/r:System.Drawing.dll", "/r:System.Web.Extensions.dll",
        "Main.cs", "Source.cs", "Shared.cs", "Update.cs", "Settings.cs", "Skins.cs", "Skins2.cs", "Accounts.cs", "AssemblyInfo.cs")
    & $csc $gui
    if ($LASTEXITCODE -ne 0) { throw "Stilla.exe failed to compile" }

    # The status line helper: must be a console program so Claude Code can feed it stdin and read its output
    $cli = $common + @(
        "/target:exe", "/out:$dist\Stilla.Statusline.exe",
        "/r:System.Web.Extensions.dll",
        "Bridge.cs", "Shared.cs", "AssemblyInfo.cs")
    & $csc $cli
    if ($LASTEXITCODE -ne 0) { throw "Stilla.Statusline.exe failed to compile" }
}
finally { Pop-Location }

$scripts = Join-Path $dist "scripts"
New-Item -ItemType Directory -Force $scripts | Out-Null
Copy-Item (Join-Path $root "scripts\*.py") $scripts -Force

# The installer: compile the files above into one exe as resources
$setupName = "Stilla-Setup-$version-$edition.exe"
$res = @("/resource:$dist\Stilla.exe,res/Stilla.exe", "/resource:$dist\Stilla.Statusline.exe,res/Stilla.Statusline.exe")
foreach ($py in Get-ChildItem $scripts -Filter *.py) { $res += "/resource:$($py.FullName),res/scripts/$($py.Name)" }
foreach ($doc in "LICENSE", "README.md") { if (Test-Path (Join-Path $root $doc)) { $res += "/resource:$(Join-Path $root $doc),res/$doc" } }
Push-Location (Join-Path $root "src")
try {
    $setup = $common + @(
        "/target:winexe", "/win32icon:..\assets\Stilla.ico", "/out:$dist\$setupName",
        "/main:Stilla.SetupProgram",
        "/r:System.Windows.Forms.dll", "/r:System.Drawing.dll", "/r:System.Web.Extensions.dll") + $res + @(
        "Installer.cs", "Shared.cs", "AssemblyInfo.cs")
    & $csc $setup
    if ($LASTEXITCODE -ne 0) { throw "The installer failed to compile" }
}
finally { Pop-Location }

# Portable edition: unzip and run. portable.txt makes the program keep settings and logs in a data folder next to itself
$stage = Join-Path $dist "_portable"
Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
$dir = Join-Path $stage "Stilla"
New-Item -ItemType Directory -Force (Join-Path $dir "scripts") | Out-Null
Copy-Item "$dist\Stilla.exe", "$dist\Stilla.Statusline.exe" $dir
Copy-Item "$scripts\*.py" (Join-Path $dir "scripts")
foreach ($doc in "LICENSE", "README.md", "PRIVACY.md") { if (Test-Path (Join-Path $root $doc)) { Copy-Item (Join-Path $root $doc) $dir } }
[IO.File]::WriteAllText((Join-Path $dir "portable.txt"), "", (New-Object Text.UTF8Encoding($false)))
$zip = Join-Path $dist "Stilla-$version-portable-$edition.zip"
Remove-Item $zip -Force -ErrorAction SilentlyContinue
Compress-Archive -Path $dir -DestinationPath $zip
Remove-Item $stage -Recurse -Force

Get-ChildItem $dist -File | Select-Object Name, Length | Format-Table -AutoSize
Write-Host "Done: $dist (version $version, language $edition)"
