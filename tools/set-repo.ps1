# 把项目里的 GitHub 仓库地址占位符 OWNER 换成你的用户名（更新检查、「前往更新页面」、捐赠入口都靠它）。
#   用法：powershell -ExecutionPolicy Bypass -File tools\set-repo.ps1 -Owner 你的GitHub用户名 [-Repo 仓库名]
#   会修改：src\Shared.cs 里的 RepoOwner / RepoName，以及文档里的 OWNER/Stilla 链接。改完重新运行 build.ps1。
param(
    [Parameter(Mandatory = $true)][string]$Owner,
    [string]$Repo = "Stilla"
)
$ErrorActionPreference = "Stop"
if ($Owner -notmatch '^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})$') { throw "GitHub 用户名只能包含字母、数字和连字符：$Owner" }
if ($Repo -notmatch '^[A-Za-z0-9._-]{1,100}$') { throw "仓库名里有不允许的字符：$Repo" }
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$enc = New-Object System.Text.UTF8Encoding($false)
$changed = 0
foreach ($rel in "src\Shared.cs", "README.md", "README.en.md", "README.zh-CN.md", "PRIVACY.md", "CHANGELOG.md", "CONTRIBUTING.md", "docs\USAGE.md") {
    $p = Join-Path $root $rel
    if (-not (Test-Path $p)) { continue }
    $text = [IO.File]::ReadAllText($p, [Text.Encoding]::UTF8)
    $new = $text.Replace('RepoOwner = "OWNER"', "RepoOwner = `"$Owner`"").Replace('RepoName = "Stilla"', "RepoName = `"$Repo`"").Replace("OWNER/Stilla", "$Owner/$Repo")
    if ($new -ne $text) { [IO.File]::WriteAllText($p, $new, $enc); $changed++; Write-Host "已更新 $rel" }
}
if ($changed -eq 0) { Write-Host "没有找到占位符 OWNER：可能已经替换过了。" } else { Write-Host "完成。现在运行 build.ps1 重新构建。" }