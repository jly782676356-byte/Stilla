# Replaces the GitHub repository placeholder OWNER in the project with your user name (the update check, "Go to update page" and the donate entry all depend on it).
#   Usage: powershell -ExecutionPolicy Bypass -File tools\set-repo.ps1 -Owner <your GitHub user name> [-Repo <repository name>]
#   It edits RepoOwner / RepoName in src\Shared.cs and the OWNER/Stilla links in the docs. Run build.ps1 again afterwards.
param(
    [Parameter(Mandatory = $true)][string]$Owner,
    [string]$Repo = "Stilla"
)
$ErrorActionPreference = "Stop"
if ($Owner -notmatch '^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})$') { throw "A GitHub user name may only contain letters, digits and hyphens: $Owner" }
if ($Repo -notmatch '^[A-Za-z0-9._-]{1,100}$') { throw "The repository name contains characters that are not allowed: $Repo" }
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$enc = New-Object System.Text.UTF8Encoding($false)
$changed = 0
foreach ($rel in "src\Shared.cs", "README.md", "README.zh-CN.md", "PRIVACY.md", "CHANGELOG.md", "CONTRIBUTING.md", "docs\USAGE.md") {
    $p = Join-Path $root $rel
    if (-not (Test-Path $p)) { continue }
    $text = [IO.File]::ReadAllText($p, [Text.Encoding]::UTF8)
    $new = $text.Replace('RepoOwner = "OWNER"', "RepoOwner = `"$Owner`"").Replace('RepoName = "Stilla"', "RepoName = `"$Repo`"").Replace("OWNER/Stilla", "$Owner/$Repo")
    if ($new -ne $text) { [IO.File]::WriteAllText($p, $new, $enc); $changed++; Write-Host "Updated $rel" }
}
if ($changed -eq 0) { Write-Host "The placeholder OWNER was not found: it may already have been replaced." } else { Write-Host "Done. Now run build.ps1 to rebuild." }
