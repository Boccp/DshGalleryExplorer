<#
.SYNOPSIS
  一条命令编译 DSH 画师素材库管理器到 out\dsh-gallery-explorer.exe。
.DESCRIPTION
  使用 Windows 自带 .NET Framework 4.x 的 csc.exe（C# 5），编译 src\ 下全部 .cs 文件。
  编译失败返回非零退出码。不会结束正在运行的进程，也不会部署到安装目录。
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\build.ps1
#>
param(
    [string]$OutDir = (Join-Path $PSScriptRoot 'out'),
    [switch]$DebugBuild
)
$ErrorActionPreference = 'Stop'

$netDir = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$csc = Join-Path $netDir 'csc.exe'
$wpfDir = Join-Path $netDir 'WPF'
if (-not (Test-Path $csc)) { Write-Error "找不到 csc.exe: $csc"; exit 2 }

$sources = @(Get-ChildItem -Path (Join-Path $PSScriptRoot 'src') -Recurse -Filter '*.cs' | Sort-Object FullName | ForEach-Object { $_.FullName })
$legacy = Join-Path $PSScriptRoot 'dsh-gallery-explorer.cs'
if (Test-Path $legacy) { $sources = @($legacy) + $sources }
if ($sources.Count -eq 0) { Write-Error '没有找到任何源文件'; exit 2 }

if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }
$outExe = Join-Path $OutDir 'dsh-gallery-explorer.exe'

$cscArgs = @(
    '/nologo', '/target:winexe', '/platform:anycpu', '/codepage:65001', '/utf8output',
    "/out:$outExe",
    '/r:System.dll', '/r:System.Core.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll',
    "/r:$wpfDir\PresentationCore.dll", "/r:$wpfDir\PresentationFramework.dll", "/r:$wpfDir\WindowsBase.dll", "/r:$netDir\System.Xaml.dll"
)
if ($DebugBuild) { $cscArgs += @('/debug+', '/optimize-', '/define:DEBUG') } else { $cscArgs += @('/optimize+', '/debug:pdbonly') }
$cscArgs += $sources

Write-Host "编译 $($sources.Count) 个源文件 -> $outExe" -ForegroundColor Cyan
& $csc @cscArgs
if ($LASTEXITCODE -ne 0) {
    Write-Host "编译失败 (exit $LASTEXITCODE)" -ForegroundColor Red
    exit $LASTEXITCODE
}
Write-Host "编译成功: $outExe" -ForegroundColor Green
exit 0

