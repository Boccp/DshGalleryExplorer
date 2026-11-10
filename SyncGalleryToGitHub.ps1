$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$Host.UI.RawUI.WindowTitle = "DshGalleryExplorer - 一键编译并上传更新到 GitHub"

$repoDir = $PSScriptRoot
$zipPath = Join-Path $repoDir "DshGalleryExplorer_Release.zip"
$login = "Boccp"
$repoName = "DshGalleryExplorer"

Write-Host "=========================================================" -ForegroundColor Cyan
Write-Host "   DshGalleryExplorer 一键编译 + 本机同步 + GitHub 上传" -ForegroundColor Cyan
Write-Host "=========================================================" -ForegroundColor Cyan
Write-Host "当前仓库目录: $repoDir" -ForegroundColor Gray
Write-Host ""

Set-Location $repoDir

$appVer = "1.0.0"
if (Test-Path "$repoDir\version.txt") {
    $appVer = (Get-Content "$repoDir\version.txt" -Raw -Encoding UTF8).Trim()
}
$tagName = "v$appVer"

$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$wpfDir = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\WPF"
$netDir = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319"

Write-Host "[1/3] 正在编译 dsh-gallery-explorer.exe ($tagName) ..." -ForegroundColor Yellow
Get-Process -Name "dsh-gallery-explorer" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 200

& $csc /nologo /target:winexe /optimize+ /out:"$repoDir\dsh-gallery-explorer.exe" `
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll `
    /r:"$wpfDir\PresentationCore.dll" /r:"$wpfDir\PresentationFramework.dll" /r:"$wpfDir\WindowsBase.dll" /r:"$netDir\System.Xaml.dll" `
    "$repoDir\dsh-gallery-explorer.cs"
if ($LASTEXITCODE -ne 0) { throw "dsh-gallery-explorer.exe 编译失败！" }

# 确保清理 RUNASADMIN 兼容层标记，防止弹出 UAC 确认框
$compatReg = "HKCU:\Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers"
if (Test-Path $compatReg) {
    Remove-ItemProperty -Path $compatReg -Name "$repoDir\dsh-gallery-explorer.exe" -ErrorAction SilentlyContinue
}
Write-Host "      -> 编译完成。" -ForegroundColor Green

Write-Host "[2/3] 正在读取本机 GitHub ($login) 授权凭证 ..." -ForegroundColor Yellow
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
                    if ($line -match '^password=(.+)$') {
                        $token = $matches[1].Trim()
                        break
                    }
                }
            } catch {}
            if ($token) { break }
        }
    }
}

if (-not (Test-Path "$repoDir\.git")) {
    git init -b main | Out-Null
    git remote add origin "https://github.com/$login/$repoName.git" 2>$null
}

git config user.name $login
git config user.email "$login@users.noreply.github.com"

Write-Host "[3/3] 正在提交并推送到 GitHub 仓库 ($login/$repoName) ..." -ForegroundColor Yellow
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

$proxy = $null
try {
    $tcp = New-Object System.Net.Sockets.TcpClient
    $ar = $tcp.BeginConnect("127.0.0.1", 7897, $null, $null)
    if ($ar.AsyncWaitHandle.WaitOne(300, $false) -and $tcp.Connected) { $proxy = "http://127.0.0.1:7897" }
    $tcp.Close()
} catch {}

$gitPushSucceeded = $false
try {
    $gitArgs = @("-c", "credential.helper=", "-c", "http.sslBackend=openssl")
    if ($proxy) {
        $gitArgs += @("-c", "http.proxy=$proxy", "-c", "https.proxy=$proxy")
    }
    git @gitArgs push -u origin main --force 2>$null
    if ($LASTEXITCODE -eq 0) {
        git @gitArgs push origin $tagName --force 2>$null
        if ($LASTEXITCODE -eq 0) {
            $gitPushSucceeded = $true
            Write-Host "      -> Git 直接推送完成。" -ForegroundColor Green
        }
    }
} catch {} finally {
    git remote set-url origin $cleanRemoteUrl
}

# 如果 github.com:443 直连被重置，自动通过畅通的 api.github.com (GitHub Git Data API) 同步提交树与 main/tag 引用
if (-not $gitPushSucceeded -and $token) {
    Write-Host "      -> 检测到 github.com:443 直连受阻，正在通过 api.github.com 同步仓库代码树 ..." -ForegroundColor Cyan
    $apiHeaders = @{
        Authorization = "Bearer $token"
        Accept = "application/vnd.github+json"
        "X-GitHub-Api-Version" = "2022-11-28"
        "User-Agent" = "DshGalleryExplorer-Publisher"
    }
    $apiParams = @{ Headers = $apiHeaders }
    if ($proxy) { $apiParams["Proxy"] = $proxy }

    $refInfo = Invoke-RestMethod -Method Get -Uri "https://api.github.com/repos/$login/$repoName/git/ref/heads/main" @apiParams
    $parentCommitSha = $refInfo.object.sha
    $parentCommit = Invoke-RestMethod -Method Get -Uri "https://api.github.com/repos/$login/$repoName/git/commits/$parentCommitSha" @apiParams
    $baseTreeSha = $parentCommit.tree.sha

    $trackedFiles = git ls-files
    $treeEntries = New-Object System.Collections.ArrayList
    foreach ($relPath in $trackedFiles) {
        $fullFile = Join-Path $repoDir $relPath
        if (-not (Test-Path $fullFile)) { continue }
        $bytes = [System.IO.File]::ReadAllBytes($fullFile)
        $b64 = [Convert]::ToBase64String($bytes)
        $blobBody = (@{ content = $b64; encoding = "base64" } | ConvertTo-Json -Compress)
        $blobResp = Invoke-RestMethod -Method Post -Uri "https://api.github.com/repos/$login/$repoName/git/blobs" -ContentType "application/json; charset=utf-8" -Body $blobBody @apiParams
        [void]$treeEntries.Add(@{
            path = ($relPath -replace '\\', '/')
            mode = "100644"
            type = "blob"
            sha  = $blobResp.sha
        })
    }

    $treeBody = (@{ base_tree = $baseTreeSha; tree = $treeEntries } | ConvertTo-Json -Depth 5 -Compress)
    $newTree = Invoke-RestMethod -Method Post -Uri "https://api.github.com/repos/$login/$repoName/git/trees" -ContentType "application/json; charset=utf-8" -Body $treeBody @apiParams

    $commitMsg = "Release & sync DshGalleryExplorer ($timestamp)"
    $commitBody = (@{
        message = $commitMsg
        tree    = $newTree.sha
        parents = @($parentCommitSha)
    } | ConvertTo-Json -Depth 4 -Compress)
    $newCommit = Invoke-RestMethod -Method Post -Uri "https://api.github.com/repos/$login/$repoName/git/commits" -ContentType "application/json; charset=utf-8" -Body $commitBody @apiParams

    $updateRefBody = (@{ sha = $newCommit.sha; force = $true } | ConvertTo-Json -Compress)
    Invoke-RestMethod -Method Patch -Uri "https://api.github.com/repos/$login/$repoName/git/refs/heads/main" -ContentType "application/json; charset=utf-8" -Body $updateRefBody @apiParams | Out-Null
    try {
        Invoke-RestMethod -Method Patch -Uri "https://api.github.com/repos/$login/$repoName/git/refs/tags/$tagName" -ContentType "application/json; charset=utf-8" -Body $updateRefBody @apiParams | Out-Null
    } catch {}
    Set-Content -Path (Join-Path $repoDir ".last_synced_commit") -Value $newCommit.sha -NoNewline -Encoding UTF8
    Write-Host "      -> GitHub 远端 main 分支及 $tagName 标签已同步至 Commit: $($newCommit.sha.Substring(0, 7))" -ForegroundColor Green
}

# 同步覆盖到系统标准程序目录 AppData\Local\Programs\DshGalleryExplorer 以及 .dsh\tools
$instDir = "$env:LOCALAPPDATA\Programs\DshGalleryExplorer"
if (-not (Test-Path $instDir)) { New-Item -Path $instDir -ItemType Directory -Force | Out-Null }
try {
    Copy-Item -LiteralPath "$repoDir\dsh-gallery-explorer.exe" -Destination "$instDir\dsh-gallery-explorer.exe" -Force -ErrorAction SilentlyContinue
} catch {}
$liveToolsDir = Join-Path $env:USERPROFILE ".dsh\tools"
if (Test-Path $liveToolsDir) {
    try {
        Copy-Item -LiteralPath "$repoDir\dsh-gallery-explorer.cs" -Destination "$liveToolsDir\dsh-gallery-explorer.cs" -Force -ErrorAction SilentlyContinue
        Copy-Item -LiteralPath "$repoDir\dsh-gallery-explorer.exe" -Destination "$liveToolsDir\dsh-gallery-explorer.exe" -Force -ErrorAction SilentlyContinue
    } catch {}
}

# 4. 打包并同步更新 GitHub Release (v1.0.0) 附件
if ($token) {
    Write-Host "[4/4] 正在同步更新 GitHub Release ($tagName) 安装包附件 ..." -ForegroundColor Yellow
    $proxy = $null
    try {
        $tcp = New-Object System.Net.Sockets.TcpClient
        $ar = $tcp.BeginConnect("127.0.0.1", 7897, $null, $null)
        if ($ar.AsyncWaitHandle.WaitOne(300, $false) -and $tcp.Connected) { $proxy = "http://127.0.0.1:7897" }
        $tcp.Close()
    } catch {}
    $headers = @{
        Authorization = "Bearer $token"
        Accept = "application/vnd.github+json"
        "X-GitHub-Api-Version" = "2022-11-28"
        "User-Agent" = "DshGalleryExplorer-Publisher"
    }
    $invokeParams = @{ Headers = $headers }
    if ($proxy) { $invokeParams["Proxy"] = $proxy }

    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $tempZip = "$env:TEMP\DshGalleryExplorer_Release_temp.zip"
    if (Test-Path $tempZip) { Remove-Item $tempZip -Force }
    $zipArchive = [System.IO.Compression.ZipFile]::Open($tempZip, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        $filesToZip = @("README.md", "dsh-gallery-explorer.cs", "dsh-gallery-explorer.exe", "dsh_file_tags.py", "artist_purity_engine.py", "generate_folder_cover_icons.py", "SyncGalleryToGitHub.ps1", "DownloadGalleryFromGitHub.ps1")
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
Write-Host " ✅ DshGalleryExplorer 同步上传完成！" -ForegroundColor Green
Write-Host "    https://github.com/Boccp/DshGalleryExplorer" -ForegroundColor White
Write-Host "=========================================================" -ForegroundColor Green
Start-Sleep -Seconds 3
