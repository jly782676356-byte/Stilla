# 编译 Stilla。不需要装 Visual Studio / .NET SDK：Windows 自带的 .NET Framework 4 编译器 csc.exe 就够了。
#   用法：在本文件夹里运行  powershell -ExecutionPolicy Bypass -File build.ps1
#   输出（dist\）：
#     Stilla.exe                        悬浮窗
#     Stilla.Statusline.exe             给 Claude Code 状态栏用的控制台小程序
#     scripts\                          可选的 Python 脚本
#     Stilla-Setup-<版本>-zh.exe        单文件安装包（上面这些都编译在里面）
#     Stilla-<版本>-portable-zh.zip     免安装版（解压即用，里面带 portable.txt：数据放在程序旁边）
param(
    [string]$Out = "dist"
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$edition = "zh"      # 这一份源码的界面语言：zh = 中文，en = 英文（用于安装包 / 压缩包的文件名）

$csc = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR "Microsoft.NET\Framework\v4.0.30319\csc.exe" }
if (-not (Test-Path $csc)) { throw "找不到 csc.exe（.NET Framework 4 编译器）" }

# 版本号只在一个地方写：src\Shared.cs 的 AppInfo.Version
$sharedText = [IO.File]::ReadAllText((Join-Path $root "src\Shared.cs"), [Text.Encoding]::UTF8)
$version = [regex]::Match($sharedText, 'Version = "([^"]+)"').Groups[1].Value
if (-not $version) { throw "没能从 src\Shared.cs 里读到版本号" }

$dist = Join-Path $root $Out
New-Item -ItemType Directory -Force $dist | Out-Null
$common = @("/nologo", "/codepage:65001", "/optimize+", "/warn:3", "/warnaserror+")

Push-Location (Join-Path $root "src")
try {
    # 悬浮窗：窗口程序（没有控制台）
    $gui = $common + @(
        "/target:winexe", "/win32icon:..\assets\Stilla.ico", "/out:$dist\Stilla.exe",
        "/r:System.Windows.Forms.dll", "/r:System.Drawing.dll", "/r:System.Web.Extensions.dll",
        "Main.cs", "Source.cs", "Shared.cs", "Update.cs", "Settings.cs", "Skins.cs", "Skins2.cs", "Accounts.cs", "AssemblyInfo.cs")
    & $csc $gui
    if ($LASTEXITCODE -ne 0) { throw "Stilla.exe 编译失败" }

    # 状态栏小程序：必须是控制台程序，Claude Code 才能给它送 stdin、读它的输出
    $cli = $common + @(
        "/target:exe", "/out:$dist\Stilla.Statusline.exe",
        "/r:System.Web.Extensions.dll",
        "Bridge.cs", "Shared.cs", "AssemblyInfo.cs")
    & $csc $cli
    if ($LASTEXITCODE -ne 0) { throw "Stilla.Statusline.exe 编译失败" }
}
finally { Pop-Location }

$scripts = Join-Path $dist "scripts"
New-Item -ItemType Directory -Force $scripts | Out-Null
Copy-Item (Join-Path $root "scripts\*.py") $scripts -Force

# 安装包：把上面的文件作为资源编译进一个 exe
$setupName = "Stilla-Setup-$version-$edition.exe"
$res = @("/resource:$dist\Stilla.exe,res/Stilla.exe", "/resource:$dist\Stilla.Statusline.exe,res/Stilla.Statusline.exe")
foreach ($py in Get-ChildItem $scripts -Filter *.py) { $res += "/resource:$($py.FullName),res/scripts/$($py.Name)" }
foreach ($doc in "LICENSE", "README.zh-CN.md") { if (Test-Path (Join-Path $root $doc)) { $res += "/resource:$(Join-Path $root $doc),res/$doc" } }
Push-Location (Join-Path $root "src")
try {
    $setup = $common + @(
        "/target:winexe", "/win32icon:..\assets\Stilla.ico", "/out:$dist\$setupName",
        "/main:Stilla.SetupProgram",
        "/r:System.Windows.Forms.dll", "/r:System.Drawing.dll", "/r:System.Web.Extensions.dll") + $res + @(
        "Installer.cs", "Shared.cs", "AssemblyInfo.cs")
    & $csc $setup
    if ($LASTEXITCODE -ne 0) { throw "安装包编译失败" }
}
finally { Pop-Location }

# 免安装版：解压即用。portable.txt 让程序把设置和日志放在自己旁边的 data 文件夹
$stage = Join-Path $dist "_portable"
Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
$dir = Join-Path $stage "Stilla"
New-Item -ItemType Directory -Force (Join-Path $dir "scripts") | Out-Null
Copy-Item "$dist\Stilla.exe", "$dist\Stilla.Statusline.exe" $dir
Copy-Item "$scripts\*.py" (Join-Path $dir "scripts")
foreach ($doc in "LICENSE", "README.zh-CN.md", "PRIVACY.md") { if (Test-Path (Join-Path $root $doc)) { Copy-Item (Join-Path $root $doc) $dir } }
[IO.File]::WriteAllText((Join-Path $dir "portable.txt"), "", (New-Object Text.UTF8Encoding($false)))
$zip = Join-Path $dist "Stilla-$version-portable-$edition.zip"
Remove-Item $zip -Force -ErrorAction SilentlyContinue
Compress-Archive -Path $dir -DestinationPath $zip
Remove-Item $stage -Recurse -Force

Get-ChildItem $dist -File | Select-Object Name, Length | Format-Table -AutoSize
Write-Host "完成：$dist（版本 $version，语言 $edition）"
