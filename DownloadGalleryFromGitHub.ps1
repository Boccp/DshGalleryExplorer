$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$Host.UI.RawUI.WindowTitle = "DshGalleryExplorer - 检查并从 GitHub 下载更新（防旧版本降级保护）"

$repoDir = $PSScriptRoot
$login = "Boccp"
$repoName = "DshGalleryExplorer"

Write-Host "=========================================================" -ForegroundColor Cyan
Write-Host "   DshGalleryExplorer 远端版本检测与安全更新下载器" -ForegroundColor Cyan
Write-Host "=========================================================" -ForegroundColor Cyan
Write-Host "本地目标目录: $repoDir" -ForegroundColor Gray
Write-Host ""

function Parse-SemVer([string]$verText) {
    if ([string]::IsNullOrWhiteSpace($verText)) { return [version]"0.0.0.0" }
    $clean = $verText.Trim().TrimStart('v', 'V')
    if ($clean -match '(\d+\.\d+(?:\.\d+){0,2})') {
        $vStr = $matches[1]
        $parts = $vStr.Split('.')
        while ($parts.Count -lt 4) { $parts += "0" }
        try { return [version]($parts[0..3] -join '.') } catch {}
    }
    return [version]"0.0.0.0"
}

$localVer = [version]"1.0.0.0"
$verFile = Join-Path $repoDir "version.txt"
if (Test-Path $verFile) {
    $localVer = Parse-SemVer (Get-Content $verFile -Raw -Encoding UTF8)
}

$localCsSha = ""
$localTimeUtc = [datetime]::MinValue
$csPath = Join-Path $repoDir "dsh-gallery-explorer.cs"
if (Test-Path $csPath) {
    $localTimeUtc = (Get-Item $csPath).LastWriteTimeUtc
    $bytes = [System.IO.File]::ReadAllBytes($csPath)
    $header = [System.Text.Encoding]::ASCII.GetBytes("blob $($bytes.Length)`0")
    $combined = New-Object byte[] ($header.Length + $bytes.Length)
    [System.Array]::Copy($header, 0, $combined, 0, $header.Length)
    [System.Array]::Copy($bytes, 0, $combined, $header.Length, $bytes.Length)
    $sha1 = [System.Security.Cryptography.SHA1]::Create()
    $hashBytes = $sha1.ComputeHash($combined)
    $localCsSha = ($hashBytes | ForEach-Object { $_.ToString("x2") }) -join ""
}

$commitFile = Join-Path $repoDir ".last_synced_commit"
$lastCommitSha = ""
$lastCommitTimeUtc = [datetime]::MinValue
if (Test-Path $commitFile) {
    $lines = Get-Content $commitFile -Encoding UTF8
    if ($lines.Count -ge 1) { $lastCommitSha = $lines[0].Trim() }
    if ($lines.Count -ge 2) {
        try { $lastCommitTimeUtc = [datetime]::Parse($lines[1].Trim()).ToUniversalTime() } catch {}
    }
}

$proxy = $null
try {
    $tcp = New-Object System.Net.Sockets.TcpClient
    $ar = $tcp.BeginConnect("127.0.0.1", 7897, $null, $null)
    if ($ar.AsyncWaitHandle.WaitOne(300, $false) -and $tcp.Connected) {
        $proxy = "http://127.0.0.1:7897"
    }
    $tcp.Close()
} catch {}

$headers = @{
    Accept = "application/vnd.github+json"
    "User-Agent" = "DshGalleryExplorer-VersionChecker"
}
$invokeParams = @{ Headers = $headers }
if ($proxy) { $invokeParams["Proxy"] = $proxy }

Write-Host "[1/2] 正在检测本地版本与 GitHub ($login/$repoName) 远端版本 ..." -ForegroundColor Yellow
Write-Host ("      本地当前版本 : v{0}" -f $localVer.ToString(3)) -ForegroundColor White

$remoteVer = [version]"1.0.0.0"
try {
    $latestRel = Invoke-RestMethod -Method Get -Uri "https://api.github.com/repos/$login/$repoName/releases/latest" @invokeParams
    if ($latestRel -and $latestRel.tag_name) {
        $remoteVer = Parse-SemVer $latestRel.tag_name
    }
} catch {}

$commits = Invoke-RestMethod -Method Get -Uri "https://api.github.com/repos/$login/$repoName/commits?per_page=1" @invokeParams
$headCommit = $commits[0]
$remoteCommitSha = $headCommit.sha
$remoteCommitShort = $remoteCommitSha.Substring(0, 7)
$remoteCommitTimeUtc = [datetime]::Parse($headCommit.commit.committer.date).ToUniversalTime()
$remoteCommitMsg = ($headCommit.commit.message -split "`n")[0].Trim()

$tree = Invoke-RestMethod -Method Get -Uri "https://api.github.com/repos/$login/$repoName/git/trees/$remoteCommitSha`?recursive=1" @invokeParams
$remoteCsBlobSha = ""
foreach ($item in $tree.tree) {
    if ($item.path -eq "dsh-gallery-explorer.cs") {
        $remoteCsBlobSha = $item.sha
    }
    if ($item.path -eq "version.txt") {
        $blob = Invoke-RestMethod -Method Get -Uri $item.url @invokeParams
        $text = [System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String($blob.content))
        $v = Parse-SemVer $text
        if ($v -gt $remoteVer) { $remoteVer = $v }
    }
}

Write-Host ("      远端最新版本 : v{0} (Commit: {1} @ {2} UTC)" -f $remoteVer.ToString(3), $remoteCommitShort, $remoteCommitTimeUtc.ToString("yyyy-MM-dd HH:mm:ss")) -ForegroundColor Cyan
Write-Host ("      远端提交说明 : {0}" -f $remoteCommitMsg) -ForegroundColor Gray
Write-Host ""

if ($remoteVer -lt $localVer) {
    Write-Host "⛔ [防降级拦截] 云端版本 (v$($remoteVer.ToString(3))) 低于本地版本 (v$($localVer.ToString(3)))，禁止覆盖！" -ForegroundColor Red
    Start-Sleep -Seconds 3
    exit 0
}

if ($remoteVer -eq $localVer) {
    if ($localCsSha -and $remoteCsBlobSha -and ($localCsSha -eq $remoteCsBlobSha)) {
        Write-Host "⛔ [防重复/防降级拦截] 云端版本 (v$($remoteVer.ToString(3)), $remoteCommitShort) 没有比本地更高（完全一致），不执行覆盖。" -ForegroundColor Yellow
        Start-Sleep -Seconds 3
        exit 0
    }
    if ($lastCommitSha -eq $remoteCommitSha -and $localTimeUtc -gt $remoteCommitTimeUtc) {
        Write-Host "⛔ [防降级拦截] 本地包含基于 $remoteCommitShort 的更新修改，云端版本未高于本地，禁止覆盖！" -ForegroundColor Red
        Start-Sleep -Seconds 4
        exit 0
    }
    if ($lastCommitTimeUtc -ge $remoteCommitTimeUtc) {
        Write-Host "⛔ [防降级拦截] 云端版本时间未高于本地已同步版本，禁止覆盖！" -ForegroundColor Red
        Start-Sleep -Seconds 3
        exit 0
    }
}

Write-Host "[2/2] 检测到云端有更高版本 ($remoteCommitShort)，正在下载覆盖到本地 ..." -ForegroundColor Yellow
Get-Process -Name "dsh-gallery-explorer" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 200

foreach ($item in $tree.tree) {
    if ($item.type -eq "blob") {
        # Keep local DownloadGalleryFromGitHub.ps1 intact so local post-download hooks are never overwritten
        if ($item.path -eq "DownloadGalleryFromGitHub.ps1") { continue }
        $blob = Invoke-RestMethod -Method Get -Uri $item.url @invokeParams
        $bytes = [System.Convert]::FromBase64String($blob.content)
        $outPath = Join-Path $repoDir $item.path
        $outDir = Split-Path -Parent $outPath
        if ($outDir -and -not (Test-Path $outDir)) {
            New-Item -Path $outDir -ItemType Directory -Force | Out-Null
        }
        if ($item.path -match '\.exe$' -and (Test-Path $outPath)) {
            try {
                [System.IO.File]::WriteAllBytes($outPath, $bytes)
            } catch {
                $oldTmp = "$outPath.old"
                Remove-Item -LiteralPath $oldTmp -Force -ErrorAction SilentlyContinue
                Move-Item -LiteralPath $outPath -Destination $oldTmp -Force
                [System.IO.File]::WriteAllBytes($outPath, $bytes)
            }
        } else {
            [System.IO.File]::WriteAllBytes($outPath, $bytes)
        }
        Write-Host "      -> 已更新文件: $($item.path) ($($bytes.Length) bytes)" -ForegroundColor Green
    }
}

# 自动清理 Windows 兼容性提权标记 (RUNASADMIN) 与网络下载锁定流 (Zone.Identifier)，防止打开时弹出 UAC / 危害确认窗口
$exeFullPath = Join-Path $repoDir "dsh-gallery-explorer.exe"
$compatReg = "HKCU:\Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers"
if (Test-Path $compatReg) {
    Remove-ItemProperty -Path $compatReg -Name $exeFullPath -ErrorAction SilentlyContinue
}
Get-ChildItem -LiteralPath $repoDir -File -ErrorAction SilentlyContinue | ForEach-Object {
    Unblock-File -LiteralPath $_.FullName -ErrorAction SilentlyContinue
}

# 将源码中的硬编码 C:\Users\Administrator\.dsh 兼容替换为动态当前用户目录并本机重新编译，确保多机同步通用
$csFullPath = Join-Path $repoDir "dsh-gallery-explorer.cs"
if (Test-Path $csFullPath) {
    $csText = Get-Content -LiteralPath $csFullPath -Raw -Encoding UTF8
    # 将源码中的硬编码路径与视频缩略图提取逻辑自动优化（避免触发 Windows Store HEVC 扩展安装弹窗）
    if ($csText -match 'C:\\Users\\Administrator\\\.dsh') {
        $csText = $csText.Replace('@"C:\Users\Administrator\.dsh\gallery_cache\thumbnails"', 'Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), @".dsh\gallery_cache\thumbnails")')
        $csText = $csText.Replace('@"C:\Users\Administrator\.dsh\gallery_cache\video_reels"', 'Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), @".dsh\gallery_cache\video_reels")')
        $csText = $csText.Replace('const string LEDGER_PATH = @"C:\Users\Administrator\.dsh\gallery_cache\_dsh_tags_ledger.json";', 'static readonly string LEDGER_PATH = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), @".dsh\gallery_cache\_dsh_tags_ledger.json");')
        $csText = $csText.Replace('const string SHORTCUTS_PATH = @"C:\Users\Administrator\.dsh\gallery_cache\_dsh_sidebar_shortcuts.json";', 'static readonly string SHORTCUTS_PATH = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), @".dsh\gallery_cache\_dsh_sidebar_shortcuts.json");')
        $csText = $csText.Replace('const string TAG_INDEX_DIR = @"C:\Users\Administrator\.dsh\gallery_cache\tags_index";', 'static readonly string TAG_INDEX_DIR = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), @".dsh\gallery_cache\tags_index");')
        $csText = $csText.Replace(': (Directory.Exists(@"F:\Openclow\画师素材库") ? @"F:\Openclow\画师素材库" : @"F:\");', ': (Directory.Exists(@"F:\Openclow\画师素材库") ? @"F:\Openclow\画师素材库" : (Directory.Exists(@"F:\") ? @"F:\" : (Directory.Exists(@"D:\") ? @"D:\" : Environment.GetFolderPath(Environment.SpecialFolder.Desktop))));')
        Set-Content -LiteralPath $csFullPath -Value $csText -Encoding UTF8
    }
    $csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
    $wpfDir = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\WPF"
    $netDir = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319"
    if (Test-Path $exeFullPath) {
        try {
            Remove-Item -LiteralPath $exeFullPath -Force -ErrorAction Stop
        } catch {
            $oldTmp = "$exeFullPath.old"
            Remove-Item -LiteralPath $oldTmp -Force -ErrorAction SilentlyContinue
            Move-Item -LiteralPath $exeFullPath -Destination $oldTmp -Force
        }
    }
    & $csc /nologo /target:winexe /optimize+ /out:$exeFullPath `
        /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll `
        /r:"$wpfDir\PresentationCore.dll" /r:"$wpfDir\PresentationFramework.dll" /r:"$wpfDir\WindowsBase.dll" /r:"$netDir\System.Xaml.dll" `
        $csFullPath

    # 同步覆盖到系统标准程序目录 AppData\Local\Programs\DshGalleryExplorer，避免 SmartScreen 拦截
    $instDir = "$env:LOCALAPPDATA\Programs\DshGalleryExplorer"
    if (-not (Test-Path $instDir)) { New-Item -Path $instDir -ItemType Directory -Force | Out-Null }
    try {
        Copy-Item -LiteralPath $exeFullPath -Destination "$instDir\dsh-gallery-explorer.exe" -Force
    } catch {}
}

Set-Content -Path (Join-Path $repoDir "version.txt") -Value $remoteVer.ToString(3) -Encoding UTF8
Set-Content -Path (Join-Path $repoDir ".last_synced_commit") -Value @($remoteCommitSha, $remoteCommitTimeUtc.ToString("o")) -Encoding UTF8

Write-Host ""
Write-Host "=========================================================" -ForegroundColor Green
Write-Host " ✅ 下载并覆盖完成！当前已是最新 v$($remoteVer.ToString(3)) ($remoteCommitShort)" -ForegroundColor Green
Write-Host "=========================================================" -ForegroundColor Green
Start-Sleep -Seconds 3
