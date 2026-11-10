# Turns the build output in dist\ into a folder that can be uploaded straight to a GitHub Release:
#   the installer, the portable zip, a SHA256SUMS file and the release notes (taken from the section of CHANGELOG.md for this version).
#   Usage: run build.ps1 first, then  powershell -ExecutionPolicy Bypass -File tools\make-release.ps1 -OutDir <folder>
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
    if (-not (Test-Path $p)) { throw "Could not find $p : run build.ps1 first" }
    Copy-Item $p $OutDir -Force
}

# Checksums in sha256sum format ("<hash>  <file name>"); users can verify with certutil / sha256sum
$lines = foreach ($a in $assets) { "{0}  {1}" -f (Get-FileHash (Join-Path $OutDir $a) -Algorithm SHA256).Hash.ToLower(), $a }
[IO.File]::WriteAllText((Join-Path $OutDir "SHA256SUMS-$edition.txt"), (($lines -join "`n") + "`n"), (New-Object Text.UTF8Encoding($false)))

# Release notes: the "## <version>" section of CHANGELOG.md
$cl = [IO.File]::ReadAllText((Join-Path $root "CHANGELOG.md"), [Text.Encoding]::UTF8)
$m = [regex]::Match($cl, "(?ms)^## $([regex]::Escape($version))[^\r\n]*\r?\n(.*?)(?=^## |\z)")
$notes = if ($m.Success) { $m.Groups[1].Value.Trim() } else { "(CHANGELOG.md has no section for $version yet)" }
$sums = ($lines | ForEach-Object { "    $_" }) -join "`n"
$body = @"
# Stilla $version

$notes

## Downloads

- ``$($assets[0])``: installer (no administrator rights needed)
- ``$($assets[1])``: portable edition (unzip and run)

The installer / zip are **not code-signed**, so Windows SmartScreen may warn about an "unknown publisher". You can verify the files were not tampered with using these checksums:

$sums

> Stilla is an independent community project. It is not made or endorsed by Anthropic.
"@
[IO.File]::WriteAllText((Join-Path $OutDir "RELEASE_NOTES-$edition.md"), $body.Replace("`r`n", "`n"), (New-Object Text.UTF8Encoding($false)))
Get-ChildItem $OutDir | Select-Object Name, Length | Format-Table -AutoSize
