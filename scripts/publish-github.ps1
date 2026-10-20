# 发布到 GitHub Releases——Release 里的资产本身就是自动更新源，发一次 = 发一个版本。
# 前置：先跑 scripts\build-installer.ps1 出包（dist\ 里要有 RELEASES）。
# token：优先级 -Token 参数 > 环境变量 VPK_GITHUB_TOKEN > 仓库根 .vpk-github-token 文件（已 gitignore）。
# 用法：
#   powershell -ExecutionPolicy Bypass -File scripts\publish-github.ps1              # 正式发布（版本号取自 csproj）
#   powershell -ExecutionPolicy Bypass -File scripts\publish-github.ps1 -Merge       # 发新版后与已有 Release 资产合并（增量包依赖旧资产共存）
#   powershell -ExecutionPolicy Bypass -File scripts\publish-github.ps1 -Draft       # 只建草稿，去网页确认后手动发布
param(
    [switch]$Merge,
    [switch]$Draft,
    [string]$Token = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot   # scripts\ 的上一级 = 仓库根
$dist = Join-Path $root "dist"
$repoUrl = "https://github.com/ACoolOctober/VigilWatt"

# 1) token：-Token 参数 > 环境变量 > 本地文件
if ($Token -eq "") { $Token = $env:VPK_GITHUB_TOKEN }
if ($Token -eq "") {
    $tokenFile = Join-Path $root ".vpk-github-token"
    if (Test-Path $tokenFile) { $Token = (Get-Content $tokenFile -Raw).Trim() }
}
if ($Token -eq "") {
    throw "找不到 GitHub token。三选一：把 PAT 存到 $root\.vpk-github-token（一行，已 gitignore）；设环境变量 VPK_GITHUB_TOKEN；或 -Token 传入"
}

# 2) 版本号：与 build-installer.ps1 同源，读 csproj 的 <Version>，截成 3 段 SemVer2
$csproj = Join-Path $root "src\VigilWatt.App\VigilWatt.App.csproj"
$version = ([xml](Get-Content $csproj -Raw)).Project.PropertyGroup.Version | Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($version)) { throw "csproj 里没找到 <Version>" }
$parts = $version.Split('.')
if ($parts.Length -gt 3) { $version = $parts[0..2] -join '.' }
Write-Host "==> 发布版本: $version  (tag: v$version)"

# 3) 确认产物存在
if (-not (Test-Path (Join-Path $dist "RELEASES"))) {
    throw "$dist 里没有发布产物，先跑 scripts\build-installer.ps1"
}

# 4) 上传。不加 --publish 只建草稿（-Draft）；--merge 允许与已有 Release 的资产合并
$vpkArgs = @(
    "upload", "github",
    "--repoUrl", $repoUrl,
    "--token", $Token,
    "--outputDir", $dist,
    "--releaseName", "VigilWatt $version",
    "--tag", "v$version"
)
if (-not $Draft) { $vpkArgs += "--publish" }
if ($Merge) { $vpkArgs += "--merge" }

vpk @vpkArgs
if ($LASTEXITCODE -ne 0) { throw "vpk upload 失败" }

Write-Host "==> 完成: $repoUrl/releases/tag/v$version"
Write-Host "    用户侧：设置页'更新源'填 $repoUrl 即可收到此更新"
