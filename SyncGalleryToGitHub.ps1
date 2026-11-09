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

Write-Host ""
Write-Host "=========================================================" -ForegroundColor Green
Write-Host " ✅ DshGalleryExplorer 同步上传完成！" -ForegroundColor Green
Write-Host "    https://github.com/Boccp/DshGalleryExplorer" -ForegroundColor White
Write-Host "=========================================================" -ForegroundColor Green
Start-Sleep -Seconds 3
