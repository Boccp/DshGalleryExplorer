param(
    [switch]$Force,
    [switch]$NoPause
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$Host.UI.RawUI.WindowTitle = "DshGalleryExplorer - 一键编译并推送到 GitHub（带防旧版本倒灌云端保护）"

$repoDir = $PSScriptRoot
$zipPath = Join-Path $repoDir "DshGalleryExplorer_Release.zip"
$login = "Boccp"
$repoName = "DshGalleryExplorer"

Write-Host "=========================================================" -ForegroundColor Cyan
Write-Host "   DshGalleryExplorer 版本防呆检测 + 编译 + 推送到 GitHub" -ForegroundColor Cyan
Write-Host "=========================================================" -ForegroundColor Cyan
Write-Host "当前仓库目录: $repoDir" -ForegroundColor Gray
Write-Host ""

function Pause-IfNeeded([int]$sec = 3) {
    if (-not $NoPause) { Start-Sleep -Seconds $sec }
}

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

function Get-GitBlobSha([string]$filePath) {
    if (-not (Test-Path $filePath)) { return "" }
    $bytes = [System.IO.File]::ReadAllBytes($filePath)
    $header = [System.Text.Encoding]::ASCII.GetBytes("blob $($bytes.Length)`0")
    $combined = New-Object byte[] ($header.Length + $bytes.Length)
    [System.Array]::Copy($header, 0, $combined, 0, $header.Length)
    [System.Array]::Copy($bytes, 0, $combined, $header.Length, $bytes.Length)
    $sha1 = [System.Security.Cryptography.SHA1]::Create()
    $hashBytes = $sha1.ComputeHash($combined)
    return ($hashBytes | ForEach-Object { $_.ToString("x2") }) -join ""
}

Set-Location $repoDir

$appVer = "1.0.0"
if (Test-Path "$repoDir\version.txt") {
    $appVer = (Get-Content "$repoDir\version.txt" -Raw -Encoding UTF8).Trim()
}
$localVer = Parse-SemVer $appVer
$tagName = "v$($localVer.ToString(3))"

$proxy = $null
try {
    $tcp = New-Object System.Net.Sockets.TcpClient
    $ar = $tcp.BeginConnect("127.0.0.1", 7897, $null, $null)
    if ($ar.AsyncWaitHandle.WaitOne(300, $false) -and $tcp.Connected) { $proxy = "http://127.0.0.1:7897" }
    $tcp.Close()
} catch {}

Write-Host "[1/4] 正在读取本机 GitHub ($login) 授权凭证并执行防倒灌版本校验 ..." -ForegroundColor Yellow
$token = $env:GITHUB_TOKEN
$tokenFile = Join-Path $repoDir ".github_token"
if (-not $token -and (Test-Path $tokenFile)) {
    $token = (Get-Content $tokenFile -Raw -Encoding UTF8).Trim()
}
if (-not $token) {
    $gcmCandidates = @(
        "C:\Users\Administrator\AppData\Local\hermes\tools\git-2.53.0+3-win32-x64\mingw64\bin\git-credential-manager.exe",
        "C:\Users\Admin\AppData\Local\hermes\tools\git-2.53.0+3-win32-x64\mingw64\bin\git-credential-manager.exe",
        "C:\Program Files\Git\mingw64\bin\git-credential-manager.exe"
    )
    foreach ($gcm in $gcmCandidates) {
        if (Test-Path $gcm) {
            try {
                $env:GCM_INTERACTIVE = "never"
                $credInput = "protocol=https`nhost=github.com`nusername=$login`n`n"
                $credLines = $credInput | & $gcm get 2>&1
                foreach ($line in $credLines) {
                    if ($line -match '^password=(.+)$') { $token = $matches[1].Trim(); break }
                }
            } catch {}
            if ($token) { break }
        }
    }
}

$headers = @{
    Accept = "application/vnd.github+json"
    "User-Agent" = "DshGalleryExplorer-Publisher"
}
if ($token) { $headers["Authorization"] = "Bearer $token" }
$invokeParams = @{ Headers = $headers }
if ($proxy) { $invokeParams["Proxy"] = $proxy }

# 远端版本与 Commit 防倒灌检查
$remoteCommitSha = ""
$remoteCsBlobSha = ""
$remoteVer = [version]"1.0.0.0"
try {
    $commits = Invoke-RestMethod -Method Get -Uri "https://api.github.com/repos/$login/$repoName/commits?per_page=1" @invokeParams
    $remoteCommitSha = $commits[0].sha
    $remoteCommitTimeUtc = [datetime]::Parse($commits[0].commit.committer.date).ToUniversalTime()
    $tree = Invoke-RestMethod -Method Get -Uri "https://api.github.com/repos/$login/$repoName/git/trees/$remoteCommitSha`?recursive=1" @invokeParams
    foreach ($item in $tree.tree) {
        if ($item.path -eq "dsh-gallery-explorer.cs") { $remoteCsBlobSha = $item.sha }
        if ($item.path -eq "version.txt") {
            $blob = Invoke-RestMethod -Method Get -Uri $item.url @invokeParams
            $text = [System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String($blob.content))
            $v = Parse-SemVer $text
            if ($v -gt $remoteVer) { $remoteVer = $v }
        }
    }
} catch {}

$localCsSha = Get-GitBlobSha "$repoDir\dsh-gallery-explorer.cs"
$gitStatus = git status --porcelain -- "dsh-gallery-explorer.cs" "dsh_file_tags.py" "artist_purity_engine.py" "generate_folder_cover_icons.py" "README.md" "version.txt" "SyncGalleryToGitHub.ps1" "DownloadGalleryFromGitHub.ps1" 2>$null

if (-not $Force -and $remoteCommitSha) {
    if ($remoteVer -gt $localVer) {
        Write-Host "⛔ [防倒灌拦截] 云端版本 (v$($remoteVer.ToString(3))) 高于本地版本 (v$($localVer.ToString(3)))！禁止用旧版本强推覆盖云端！" -ForegroundColor Red
        Write-Host "   请先执行「从 GitHub 拉取更新到本地」。" -ForegroundColor Yellow
        Pause-IfNeeded 4
        exit 0
    }
    if ($localCsSha -eq $remoteCsBlobSha -and -not $gitStatus) {
        $localHead = (git rev-parse HEAD 2>$null).Trim()
        if ($localHead -eq $remoteCommitSha) {
            Write-Host "✅ [防重复推送拦截] 本地源码与 GitHub 云端 ($($remoteCommitSha.Substring(0,7))) 完全一致，无任何新改动需要推送。" -ForegroundColor Green
            Pause-IfNeeded 2
            exit 0
        }
    }
    # 检查云端是否有本地尚未拉取的新 Commit（零阻塞：直接用本地 Git 对象库检查云端 SHA 是否已知，未知即代表云端有新提交）
    try {
        $localHead = (git rev-parse HEAD 2>$null).Trim()
        if ($localHead -and $localHead -ne $remoteCommitSha) {
            git cat-file -e "$remoteCommitSha^{commit}" 2>$null
            if ($LASTEXITCODE -ne 0) {
                Write-Host "⛔ [防倒灌拦截] GitHub 云端存在本地尚未拉取的新提交 ($($remoteCommitSha.Substring(0,7)))！" -ForegroundColor Red
                Write-Host "   为防止强推抹掉云端新记录，请先运行「从 GitHub 拉取更新到本地」或使用 -Force。" -ForegroundColor Yellow
                Pause-IfNeeded 4
                exit 0
            }
            git merge-base --is-ancestor $remoteCommitSha HEAD 2>$null
            if ($LASTEXITCODE -ne 0) {
                Write-Host "⛔ [防倒灌拦截] GitHub 云端存在本地尚未合并的新提交 ($($remoteCommitSha.Substring(0,7)))！" -ForegroundColor Red
                Write-Host "   为防止强推抹掉云端新记录，请先运行「从 GitHub 拉取更新到本地」或使用 -Force。" -ForegroundColor Yellow
                Pause-IfNeeded 4
                exit 0
            }
        }
    } catch {}
}

$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$wpfDir = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\WPF"
$netDir = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319"

Write-Host "[2/4] 正在编译 dsh-gallery-explorer.exe ($tagName) 并同步本机运行目录 ..." -ForegroundColor Yellow
Get-Process -Name "dsh-gallery-explorer" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 200

& $csc /nologo /target:winexe /optimize+ /out:"$repoDir\dsh-gallery-explorer.exe" `
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll `
    /r:"$wpfDir\PresentationCore.dll" /r:"$wpfDir\PresentationFramework.dll" /r:"$wpfDir\WindowsBase.dll" /r:"$netDir\System.Xaml.dll" `
    "$repoDir\dsh-gallery-explorer.cs"
if ($LASTEXITCODE -ne 0) { throw "dsh-gallery-explorer.exe 编译失败！" }

$compatReg = "HKCU:\Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers"
if (Test-Path $compatReg) {
    Remove-ItemProperty -Path $compatReg -Name "$repoDir\dsh-gallery-explorer.exe" -ErrorAction SilentlyContinue
}

$instDir = "$env:LOCALAPPDATA\Programs\DshGalleryExplorer"
if (-not (Test-Path $instDir)) { New-Item -Path $instDir -ItemType Directory -Force | Out-Null }
Copy-Item -LiteralPath "$repoDir\dsh-gallery-explorer.exe" -Destination "$instDir\dsh-gallery-explorer.exe" -Force -ErrorAction SilentlyContinue
$liveToolsDir = Join-Path $env:USERPROFILE ".dsh\tools"
if (Test-Path $liveToolsDir) {
    Copy-Item -LiteralPath "$repoDir\dsh-gallery-explorer.cs" -Destination "$liveToolsDir\dsh-gallery-explorer.cs" -Force -ErrorAction SilentlyContinue
    Copy-Item -LiteralPath "$repoDir\dsh-gallery-explorer.exe" -Destination "$liveToolsDir\dsh-gallery-explorer.exe" -Force -ErrorAction SilentlyContinue
}
Write-Host "      -> 编译与本机部署完成。" -ForegroundColor Green

git config user.name $login
git config user.email "$login@users.noreply.github.com"

Write-Host "[3/4] 正在提交并推送到 GitHub 仓库 ($login/$repoName) ..." -ForegroundColor Yellow
git add -A
$status = git status --porcelain
$timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
if ($status) {
    git commit -m "Release & sync DshGalleryExplorer ($timestamp)"
}
git tag -f $tagName

$cleanRemoteUrl = "https://github.com/${login}/${repoName}.git"
if ($token) {
    git remote set-url origin "https://${login}:${token}@github.com/${login}/${repoName}.git"
} else {
    git remote set-url origin $cleanRemoteUrl
}

try {
    if ($token) {
        git -c credential.helper= -c http.sslBackend=openssl push -u origin main --force
        git -c credential.helper= -c http.sslBackend=openssl push origin $tagName --force
    } else {
        git push -u origin main --force
        git push origin $tagName --force
    }
} finally {
    git remote set-url origin $cleanRemoteUrl
}

$newHeadSha = (git rev-parse HEAD).Trim()
Set-Content -Path (Join-Path $repoDir ".last_synced_commit") -Value @($newHeadSha, (Get-Date).ToUniversalTime().ToString("o")) -Encoding UTF8

if ($token) {
    Write-Host "[4/4] 正在同步更新 GitHub Release ($tagName) 安装包附件 ..." -ForegroundColor Yellow
    $headers["X-GitHub-Api-Version"] = "2022-11-28"

    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $tempZip = "$env:TEMP\DshGalleryExplorer_Release_temp.zip"
    if (Test-Path $tempZip) { Remove-Item $tempZip -Force }
    $zipArchive = [System.IO.Compression.ZipFile]::Open($tempZip, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        $filesToZip = @("README.md", "version.txt", "dsh-gallery-explorer.cs", "dsh-gallery-explorer.exe", "dsh_file_tags.py", "artist_purity_engine.py", "generate_folder_cover_icons.py", "SyncGalleryToGitHub.ps1", "DownloadGalleryFromGitHub.ps1")
        foreach ($f in $filesToZip) {
            $full = Join-Path $repoDir $f
            if (Test-Path $full) {
                [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zipArchive, $full, $f) | Out-Null
            }
        }
    } finally {
        $zipArchive.Dispose()
    }
    Move-Item -Path $tempZip -Destination $zipPath -Force

    try {
        $release = Invoke-RestMethod -Method Get -Uri "https://api.github.com/repos/$login/$repoName/releases/tags/$tagName" @invokeParams
        $uploadUrlBase = ($release.upload_url -replace '\{\?name,label\}$', '')
        $existingAssets = Invoke-RestMethod -Method Get -Uri "https://api.github.com/repos/$login/$repoName/releases/$($release.id)/assets" @invokeParams
        foreach ($assetName in @("dsh-gallery-explorer.exe", "DshGalleryExplorer_Release.zip")) {
            $oldAsset = $existingAssets | Where-Object { $_.name -eq $assetName }
            if ($oldAsset) {
                Invoke-RestMethod -Method Delete -Uri "https://api.github.com/repos/$login/$repoName/releases/assets/$($oldAsset.id)" @invokeParams | Out-Null
            }
        }
        Invoke-RestMethod -Method Post -Uri "${uploadUrlBase}?name=dsh-gallery-explorer.exe" -ContentType "application/octet-stream" -InFile "$repoDir\dsh-gallery-explorer.exe" @invokeParams | Out-Null
        Invoke-RestMethod -Method Post -Uri "${uploadUrlBase}?name=DshGalleryExplorer_Release.zip" -ContentType "application/zip" -InFile $zipPath @invokeParams | Out-Null
        Write-Host "      -> Release 附件同步完成！" -ForegroundColor Green
    } catch {
        Write-Host "      -> Release 附件更新跳过: $($_.Exception.Message)" -ForegroundColor Yellow
    }
}

Write-Host ""
Write-Host "=========================================================" -ForegroundColor Green
Write-Host " ✅ DshGalleryExplorer 同步上传完成！Commit: $($newHeadSha.Substring(0,7))" -ForegroundColor Green
Write-Host "    https://github.com/Boccp/DshGalleryExplorer" -ForegroundColor White
Write-Host "=========================================================" -ForegroundColor Green
Pause-IfNeeded 3
