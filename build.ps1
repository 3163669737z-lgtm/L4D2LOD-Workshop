# =====================================================================
#  远景工坊 · L4D2 自动 LOD 工具 —— 一键构建
#  用法:
#    powershell -ExecutionPolicy Bypass -File build.ps1            # Release 构建 + 复制到 dist
#    powershell -ExecutionPolicy Bypass -File build.ps1 -Clean     # 先清理
#    powershell -ExecutionPolicy Bypass -File build.ps1 -NoDist    # 只构建不复制
#
#  编译器：便携 .NET SDK（net48 目标，用 C# 最新语言特性）。
#  查找顺序：参数 -DotNet → 环境变量 DOTNET_EXE → 默认便携路径 → PATH 里的 dotnet。
# =====================================================================
param(
    [switch]$Clean,
    [switch]$NoDist,
    [switch]$Debug,
    [string]$DotNet,
    [string]$Configuration = ""
)

$ErrorActionPreference = 'Stop'
$Root = $PSScriptRoot
$Project = Join-Path $Root 'src\L4D2LodStudio.csproj'
$DistDir = Join-Path $Root 'dist'
$BuildDir = Join-Path $Root '.build'
$Configuration = if ($Configuration) { $Configuration } elseif ($Debug) { 'Debug' } else { 'Release' }

function Fail([string]$msg) {
    Write-Host ""
    Write-Host "BUILD FAILED: $msg" -ForegroundColor Red
    exit 1
}

# ── 定位 dotnet ──
$dotnetExe = $null
$candidates = @()
if ($DotNet) { $candidates += $DotNet }
if ($env:DOTNET_EXE) { $candidates += $env:DOTNET_EXE }
$candidates += 'C:\Users\TPP\.tools\dotnet-sdk-8.0.404\dotnet.exe'
$candidates += (Join-Path $env:ProgramFiles 'dotnet\dotnet.exe')
foreach ($c in $candidates) {
    if ($c -and (Test-Path -LiteralPath $c)) { $dotnetExe = $c; break }
}
if (-not $dotnetExe) {
    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($cmd) { $dotnetExe = $cmd.Source }
}
if (-not $dotnetExe) {
    Fail "找不到 dotnet（.NET SDK）。请安装/解压 .NET 8 SDK 后，用 -DotNet <dotnet.exe 路径> 或设置环境变量 DOTNET_EXE。"
}

# ── 清理 ──
if ($Clean) {
    Write-Host "清理 $BuildDir ..."
    Remove-Item -Recurse -Force -Path (Join-Path $Root 'src\bin'), (Join-Path $Root 'src\obj'), $BuildDir -ErrorAction SilentlyContinue
}

# ── 环境 ──
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

# ── 构建 ──
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$publishDir = Join-Path $BuildDir "publish-$Configuration"
if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }

Write-Host "SDK      : $dotnetExe"
Write-Host "Project  : $Project"
Write-Host "Config   : $Configuration"
Write-Host ""

$args = @(
    'publish', $Project,
    '-c', $Configuration,
    '-o', $publishDir,
    '--nologo',
    '-v', 'minimal',
    '-p:DebugType=None',
    '-p:DebugSymbols=false',
    '-p:GenerateSupportedRuntime=false'
)
& $dotnetExe @args
if ($LASTEXITCODE -ne 0) { Fail "dotnet publish 退出码 $LASTEXITCODE" }

$exeName = 'L4D2LOD远景工坊.exe'
$builtExe = Join-Path $publishDir $exeName
if (-not (Test-Path -LiteralPath $builtExe)) {
    $any = Get-ChildItem -LiteralPath $publishDir -Filter '*.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $any) { Fail "发布目录里没有 exe：$publishDir" }
    $builtExe = $any.FullName
    $exeName = $any.Name
}

# ── 复制到 dist ──
$finalExe = $builtExe
if (-not $NoDist) {
    New-Item -ItemType Directory -Force -Path $DistDir | Out-Null
    $finalExe = Join-Path $DistDir $exeName
    $copied = $false
    for ($j = 0; $j -lt 12; $j++) {
        try {
            Copy-Item -LiteralPath $builtExe -Destination $finalExe -Force -ErrorAction Stop
            $copied = $true
            break
        } catch {
            Start-Sleep -Milliseconds 500
        }
    }
    if (-not $copied) {
        # 文件被正在运行的实例占用：Windows 允许把运行中的 exe 改名，改走后即可写入新文件
        $moved = $false
        $bak = $finalExe + '.old_' + (Get-Date -Format 'HHmmss')
        try {
            Move-Item -LiteralPath $finalExe -Destination $bak -Force -ErrorAction Stop
            $moved = $true
            Copy-Item -LiteralPath $builtExe -Destination $finalExe -Force -ErrorAction Stop
            $copied = $true
        } catch {
            if ($moved -and -not (Test-Path -LiteralPath $finalExe)) {
                try { Move-Item -LiteralPath $bak -Destination $finalExe -Force } catch { }
            }
        }
    }
    if (-not $copied) { Fail "无法写入 $finalExe（文件被占用且改名降级也失败）。请关闭正在运行的实例后重试。" }
    # 顺带部署使用说明（菜单「查看使用说明」会找 exe 旁的 README.md）
    $manual = Join-Path $Root 'README.md'
    if (Test-Path -LiteralPath $manual) {
        try { Copy-Item -LiteralPath $manual -Destination (Join-Path $DistDir 'README.md') -Force -ErrorAction Stop } catch { }
    }
    # 清理旧的改名备份（被占用的删不掉就留着，下次构建再清）
    Get-ChildItem -LiteralPath $DistDir -Filter ($exeName + '.old_*') -ErrorAction SilentlyContinue | ForEach-Object {
        try { Remove-Item -LiteralPath $_.FullName -Force -ErrorAction SilentlyContinue } catch { }
    }
}

# ── 完整性级别（Low 会写不了普通文件；csc/构建产物在受限环境里可能是 Low） ──
function Get-Integrity($path) {
    try {
        $out = & icacls $path 2>$null
        $line = $out | Select-String -Pattern 'Mandatory Label\\(\w+)' -AllMatches | Select-Object -First 1
        if ($line -and $line.Matches.Count -gt 0) { return $line.Matches[0].Groups[1].Value }
    } catch { }
    return 'Unknown'
}
$integrity = Get-Integrity $finalExe
if ($integrity -eq 'Low') {
    Write-Host "完整性级别为 Low，尝试提升到 Medium ..."
    & icacls $finalExe /setintegritylevel Medium 2>&1 | Out-Null
    $integrity = Get-Integrity $finalExe
}

# ── 摘要 ──
$sw.Stop()
$fi = Get-Item -LiteralPath $finalExe
$hash = (Get-FileHash -LiteralPath $finalExe -Algorithm SHA256).Hash
$vi = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($finalExe)

Write-Host ""
Write-Host "BUILD OK  用时 $([math]::Round($sw.Elapsed.TotalSeconds,1))s"
Write-Host "  exe        : $finalExe"
Write-Host "  size       : $($fi.Length) bytes  ($([math]::Round($fi.Length/1024,1)) KB)"
Write-Host "  file ver   : $($vi.FileVersion)   product: $($vi.ProductName)"
Write-Host "  integrity  : $integrity"
Write-Host "  sha256     : $hash"
Write-Host ""
if ($integrity -eq 'Low') {
    Write-Host "WARNING: 完整性级别仍是 Low，exe 写普通文件会失败；请手动 icacls <exe> /setintegritylevel Medium" -ForegroundColor Yellow
}
