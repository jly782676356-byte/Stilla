# 把 dist\ 里的构建产物整理成一个「可以直接传到 GitHub Release」的文件夹：
#   安装包、免安装 zip、SHA256SUMS 校验文件、Release 说明（取自 CHANGELOG.md 里这个版本那一节）。
#   用法：先运行 build.ps1，再  powershell -ExecutionPolicy Bypass -File tools\make-release.ps1 -OutDir <文件夹>
param(
    [Parameter(Mandatory = $true)][string]$OutDir
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$dist = Join-Path $root "dist"
$sharedText = [IO.File]::ReadAllText((Join-Path $root "src\Shared.cs"), [Text.Encoding]::UTF8)
$version = [regex]::Match($sharedText, 'Version = "([^"]+)"').Groups[1].Value
$buildText = [IO.File]::ReadAllText((Join-Path $root "build.ps1"), [Text.Encoding]::UTF8)
$edition = [regex]::Match($buildText, '\$edition = "([a-z]+)"').Groups[1].Value

$assets = @("Stilla-Setup-$version-$edition.exe", "Stilla-$version-portable-$edition.zip")
New-Item -ItemType Directory -Force $OutDir | Out-Null
foreach ($a in $assets) {
    $p = Join-Path $dist $a
    if (-not (Test-Path $p)) { throw "找不到 $p ：请先运行 build.ps1" }
    Copy-Item $p $OutDir -Force
}

# 校验值：sha256sum 格式（"<哈希>  <文件名>"），用户可以用 certutil / sha256sum 核对
$lines = foreach ($a in $assets) { "{0}  {1}" -f (Get-FileHash (Join-Path $OutDir $a) -Algorithm SHA256).Hash.ToLower(), $a }
[IO.File]::WriteAllText((Join-Path $OutDir "SHA256SUMS-$edition.txt"), (($lines -join "`n") + "`n"), (New-Object Text.UTF8Encoding($false)))

# Release 说明：CHANGELOG.md 里「## <版本>」那一节
$cl = [IO.File]::ReadAllText((Join-Path $root "CHANGELOG.md"), [Text.Encoding]::UTF8)
$m = [regex]::Match($cl, "(?ms)^## $([regex]::Escape($version))[^\r\n]*\r?\n(.*?)(?=^## |\z)")
$notes = if ($m.Success) { $m.Groups[1].Value.Trim() } else { "（CHANGELOG.md 里还没有 $version 这一节）" }
$sums = ($lines | ForEach-Object { "    $_" }) -join "`n"
$body = @"
# Stilla $version

$notes

## 下载

- ``$($assets[0])``：安装包（不需要管理员权限）
- ``$($assets[1])``：免安装版（解压即用）

安装包 / 压缩包**没有代码签名**，Windows SmartScreen 可能提示"未知发布者"。可以用下面的校验值核对文件没被改过：

$sums

> Stilla 是独立的社区项目，不是 Anthropic 出品，也没有得到 Anthropic 的认可。
"@
[IO.File]::WriteAllText((Join-Path $OutDir "RELEASE_NOTES-$edition.md"), $body.Replace("`r`n", "`n"), (New-Object Text.UTF8Encoding($false)))
Get-ChildItem $OutDir | Select-Object Name, Length | Format-Table -AutoSize
