# =====================================================================
#  远景工坊 · 端到端回归（真实 Blender + 合成工程）
#  用法:
#    powershell -ExecutionPolicy Bypass -File tests\run_e2e.ps1
#    powershell -ExecutionPolicy Bypass -File tests\run_e2e.ps1 -Blender "D:\blender 4.1\blender.exe"
#    ... -SkipBuild -KeepTemp -IncludeCancel
#  产物: <TEMP>\lodstudio_e2e\logs\*（每次运行的 stdout/报告）+ e2e_report.txt
#  退出码: 0 = 全部通过；1 = 有失败项
# =====================================================================
param(
    [string]$Blender = '',
    [switch]$SkipBuild,
    [switch]$KeepTemp,
    [switch]$IncludeCancel,
    [int]$Levels = 2,
    [string]$Dist = '20,50',
    [string]$Ratio = '60,25'
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot
$Exe = Join-Path $Root 'dist\L4D2LOD远景工坊.exe'
$T = Join-Path $env:TEMP 'lodstudio_e2e'
$LogDir = Join-Path $T 'logs'
$SettingsDir = Join-Path $T 'settings'
$Results = New-Object System.Collections.Generic.List[object]

function Log([string]$msg) { Write-Host $msg }

function Check([string]$name, [scriptblock]$body) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        & $body
        $sw.Stop()
        $script:Results.Add([pscustomobject]@{ Name = $name; Ok = $true; Message = ''; Ms = [math]::Round($sw.Elapsed.TotalMilliseconds) })
        Write-Host ("  PASS  {0}  ({1} ms)" -f $name, [math]::Round($sw.Elapsed.TotalMilliseconds)) -ForegroundColor DarkGreen
    } catch {
        $sw.Stop()
        $script:Results.Add([pscustomobject]@{ Name = $name; Ok = $false; Message = $_.Exception.Message; Ms = [math]::Round($sw.Elapsed.TotalMilliseconds) })
        Write-Host ("  FAIL  {0}" -f $name) -ForegroundColor Red
        Write-Host ("        " + $_.Exception.Message) -ForegroundColor Red
    }
}

function Assert([bool]$cond, [string]$msg) {
    if (-not $cond) { throw $msg }
}

function Assert-Contains([string]$text, [string]$needle, [string]$what) {
    if ($null -eq $text -or -not $text.Contains($needle)) { throw "$what 未包含「$needle」" }
}

function Assert-NotContains([string]$text, [string]$needle, [string]$what) {
    if ($null -ne $text -and $text.Contains($needle)) { throw "$what 不应包含「$needle」" }
}

function Invoke-Tool([string]$tag, [string[]]$argv) {
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $Exe
    $psi.Arguments = ($argv | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } }) -join ' '
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    # 被测 exe 的 stdout/stderr 是 UTF-8；显式指定解码，避免随控制台代码页（GBK 等）乱码导致中文断言失败
    $psi.StandardOutputEncoding = [System.Text.Encoding]::UTF8
    $psi.StandardErrorEncoding = [System.Text.Encoding]::UTF8
    $psi.CreateNoWindow = $true
    # 不在测试里读用户真实设置
    $p = [System.Diagnostics.Process]::Start($psi)
    $so = $p.StandardOutput.ReadToEnd()
    $se = $p.StandardError.ReadToEnd()
    $p.WaitForExit()
    if (-not (Test-Path $LogDir)) { New-Item -ItemType Directory -Force $LogDir | Out-Null }
    $logPath = Join-Path $LogDir (("{0}_{1}.txt" -f $tag, (Get-Date -Format 'HHmmss')))
    ($so + "`r`n--- STDERR ---`r`n" + $se + "`r`nexit=$($p.ExitCode)") | Set-Content -LiteralPath $logPath -Encoding UTF8
    return [pscustomobject]@{ Code = $p.ExitCode; Out = $so; Err = $se; Log = $logPath }
}

function Save-Json([string]$path, $obj) { $obj | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $path -Encoding UTF8 }
function Read-Json([string]$path) { Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json }

function Get-TreeHash([string]$dir) {
    $sb = New-Object System.Text.StringBuilder
    Get-ChildItem -LiteralPath $dir -Recurse -File | Sort-Object FullName | ForEach-Object {
        $rel = $_.FullName.Substring($dir.Length)
        $h = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        [void]$sb.AppendLine($rel + '|' + $h)
    }
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($sb.ToString())
    $sha = [System.Security.Cryptography.SHA256]::Create()
    return ([BitConverter]::ToString($sha.ComputeHash($bytes))).Replace('-', '')
}

function Get-FileHashText([string]$path) { (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }

function Read-TextAuto([string]$path) {
    $bytes = [System.IO.File]::ReadAllBytes($path)
    $hasBom = ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)
    try {
        $enc = New-Object System.Text.UTF8Encoding($false, $true)
        $text = $enc.GetString($bytes)
        if ($hasBom) { $text = $text.Substring(1) }
        return [pscustomobject]@{ Text = $text; Encoding = 'utf-8'; Bom = $hasBom }
    } catch {
        $gbk = [System.Text.Encoding]::GetEncoding(936)
        return [pscustomobject]@{ Text = $gbk.GetString($bytes); Encoding = 'gbk'; Bom = $false }
    }
}

function Copy-Project([string]$src, [string]$name) {
    $dst = Join-Path $T ('work\' + $name)
    if (Test-Path $dst) { Remove-Item -LiteralPath $dst -Recurse -Force }
    Copy-Item -LiteralPath $src -Destination $dst -Recurse -Force
    return $dst
}

function New-RunDir([string]$name) {
    $d = Join-Path $T ('runs\' + $name)
    if (Test-Path $d) { Remove-Item -LiteralPath $d -Recurse -Force }
    New-Item -ItemType Directory -Force $d | Out-Null
    return $d
}

# ───────────────────────────── 准备 ─────────────────────────────

Write-Host ""
Write-Host "═══ 远景工坊 E2E ═══" -ForegroundColor Cyan
Write-Host "root: $Root"
if (Test-Path $T) { Remove-Item -LiteralPath $T -Recurse -Force }
New-Item -ItemType Directory -Force $LogDir | Out-Null
New-Item -ItemType Directory -Force $SettingsDir | Out-Null
$env:L4D2LODSTUDIO_SETTINGS_DIR = $SettingsDir

$pyCandidates = @(
    'C:\Users\TPP\.dsh\dsh-runtimes\dsh-primary-runtime\dependencies\python\python.exe',
    'python')
$py = $null
foreach ($c in $pyCandidates) {
    if ($c -eq 'python') { $cmd = Get-Command python -ErrorAction SilentlyContinue; if ($cmd) { $py = $cmd.Source; break } }
    elseif (Test-Path -LiteralPath $c) { $py = $c; break }
}
if (-not $py) { throw '找不到 Python（生成测试数据需要）。' }

$blenderCandidates = @()
if ($Blender) { $blenderCandidates += $Blender }
$blenderCandidates += 'D:\blender 4.1\blender.exe'
$blenderCandidates += 'D:\Blender 4.0\blender.exe'
$blenderExe = $null
foreach ($b in $blenderCandidates) { if ($b -and (Test-Path -LiteralPath $b)) { $blenderExe = $b; break } }
if (-not $blenderExe) { throw '找不到 Blender（E2E 需要真实 Blender）。用 -Blender 指定。' }
Write-Host "blender: $blenderExe"
Write-Host "python : $py"

if (-not $SkipBuild) {
    Log "构建..."
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $Root 'build.ps1') | Out-Host
    if ($LASTEXITCODE -ne 0) { throw '构建失败' }
}
if (-not (Test-Path -LiteralPath $Exe)) { throw "找不到 exe: $Exe" }
# 把被测 exe 复制到纯 ASCII 临时目录再跑：避免与后续构建抢占 dist 文件
$TestExeDir = Join-Path $T 'exe'
New-Item -ItemType Directory -Force $TestExeDir | Out-Null
$TestExe = Join-Path $TestExeDir 'L4D2LOD远景工坊.exe'
Copy-Item -LiteralPath $Exe -Destination $TestExe -Force
$Exe = $TestExe
Write-Host ("test exe: " + $Exe)

Log "生成测试数据（真实 Blender）..."
$genOut = Join-Path $T 'fixtures'
& $py (Join-Path $Root 'tests\gen_fixtures.py') --blender $blenderExe --out $genOut --force 2>&1 | Tee-Object -FilePath (Join-Path $LogDir 'gen_fixtures.log') | Out-Host
if ($LASTEXITCODE -ne 0) { throw '测试数据生成失败' }
$fx = Read-Json (Join-Path $genOut 'fixtures.json')
$P = $fx.projects
Write-Host ("工程: main={0}" -f $P.main)

$common = @('--levels', "$Levels", '--dist', $Dist, '--ratio', $Ratio)

# ───────────────────────────── 用例 ─────────────────────────────

Check 'selftest：引擎自测 0 失败' {
    $r = Invoke-Tool 'selftest' @('--selftest', '--out', (Join-Path $T 'selftest'))
    Assert ($r.Code -eq 0) ("selftest 退出码 " + $r.Code + "；" + (($r.Out.Split("`n") | Select-Object -Last 6) -join " / "))
    Assert-Contains $r.Out 'SELFTEST:' 'selftest 输出'
    $line = ($r.Out -split "`n" | Where-Object { $_ -like 'SELFTEST:*' } | Select-Object -Last 1)
    Assert-Contains $line '0 failed' '自测结果'
}

Check 'plan：只读计划不写任何文件' {
    $work = Copy-Project $P.main 'plan_main'
    $before = Get-TreeHash $work
    $out = New-RunDir 'plan_main'
    $r = Invoke-Tool 'plan_main' (@('--plan', $work, '--out', $out) + $common)
    Assert ($r.Code -eq 0) ("plan 退出码 " + $r.Code + "`n" + $r.Out)
    $after = Get-TreeHash $work
    Assert ($before -eq $after) 'plan 阶段修改了工程文件（应为只读）'
    $pj = Read-Json (Join-Path $out 'plan.json')
    Assert ($pj.counts.qc -eq 3) ("根 QC 数应为 3，实际 " + $pj.counts.qc)
    Assert ($pj.counts.jobs -ge 4) ("减面任务过少: " + $pj.counts.jobs)
    Assert ($pj.counts.process -ge 2) ("可处理 QC 过少: " + $pj.counts.process)
}

Check 'batch 模式0：生成 LOD + 注入 $lod + 备份 + 幂等' {
    $work = Copy-Project $P.main 'main_inline'
    $out1 = New-RunDir 'main_inline_1'
    $qcBefore = @{}
    Get-ChildItem -LiteralPath (Join-Path $work 'qc') -Filter '*.qc' | ForEach-Object { $qcBefore[$_.Name] = Get-FileHashText $_.FullName }
    $r1 = Invoke-Tool 'batch_main_1' (@('--batch', $work, '--out', $out1) + $common)
    Assert ($r1.Code -eq 0) ("batch 退出码 " + $r1.Code + "；" + ((($r1.Out -split "`n") | Select-Object -Last 20) -join " / "))
    $b1 = Read-Json (Join-Path $out1 'batch.json')
    Assert ($b1.ok -eq $true) 'batch.json ok != true'
    Assert ($b1.stats.injectedQc -eq 3) ("应注入 3 个 QC，实际 " + $b1.stats.injectedQc)
    Assert ($b1.stats.jobFail -eq 0) ("减面失败数 " + $b1.stats.jobFail)

    # LOD 文件：char_body / armored / crate × 2 级
    foreach ($m in @('char_body', 'armored', 'crate')) {
        foreach ($n in 1..$Levels) {
            $f = Join-Path $work ("models\SMD\{0}_lod{1}.glb" -f $m, $n)
            Assert (Test-Path -LiteralPath $f) ("缺少 LOD 文件: " + $f)
            Assert ((Get-Item -LiteralPath $f).Length -gt 200) ("LOD 文件过小: " + $f)
        }
    }

    # QC 注入内容：actor.qc 只 include，模型声明在 actor_main.qc → 工具块必须写 actor_main.qc，根 QC 不动
    $actorMain = Read-TextAuto (Join-Path $work 'qc\actor_main.qc')
    Assert-Contains $actorMain.Text '//>>> L4D2 自动 LOD 工具 生成块 开始 >>>' 'actor_main.qc 工具块开始'
    Assert-Contains $actorMain.Text '//<<< L4D2 自动 LOD 工具 生成块 结束 <<<' 'actor_main.qc 工具块结束'
    Assert-Contains $actorMain.Text '$lod 20' 'actor_main.qc 第一级距离'
    Assert-Contains $actorMain.Text 'replacemodel "SMD/char_body.glb"' 'actor_main.qc replacemodel 源名必须逐字保留'
    Assert-Contains $actorMain.Text 'nofacial' 'actor_main.qc 应写 nofacial'
    $actorRoot = Read-TextAuto (Join-Path $work 'qc\actor.qc')
    Assert-NotContains $actorRoot.Text '//>>> L4D2 自动 LOD 工具 生成块 开始 >>>' 'actor.qc（只 include）不应被写入工具块'
    Assert ((Get-FileHashText (Join-Path $work 'qc\actor.qc')) -eq $qcBefore['actor.qc']) 'actor.qc 应保持原样（块写在 include 文件里）'
    $weapon = Read-TextAuto (Join-Path $work 'qc\weapon.qc')
    Assert-Contains $weapon.Text 'replacemodel "SMD/crate"' 'weapon.qc replacemodel 必须逐字使用 QC 里写的路径'
    Assert-Contains $weapon.Text '$lod 50' 'weapon.qc 第二级距离'

    # GBK QC：编码保持
    $gbkPath = Join-Path $work 'qc\gbk_model.qc'
    $gbkBaking = Read-TextAuto $gbkPath
    Assert ($gbkBaking.Encoding -eq 'gbk') ("GBK QC 写回后编码变了: " + $gbkBaking.Encoding)
    Assert-Contains $gbkBaking.Text '中文注释' 'GBK QC 中文注释'
    Assert-Contains $gbkBaking.Text '$lod 20' 'GBK QC 注入'

    # 备份 = 运行前原件（块写在 actor_main.qc，actor.qc 未被修改所以没有备份）
    foreach ($n in @('actor_main.qc', 'weapon.qc', 'gbk_model.qc')) {
        $bak = Join-Path $work ("qc\{0}.lodbak" -f $n)
        Assert (Test-Path -LiteralPath $bak) ("缺少备份: " + $bak)
        Assert ((Get-FileHashText $bak) -eq $qcBefore[$n]) ("备份内容不等于运行前原件: " + $n)
    }
    Assert (-not (Test-Path -LiteralPath (Join-Path $work 'qc\actor.qc.lodbak'))) 'actor.qc 未被修改，不应有备份'

    # 幂等：再跑一次，QC 字节完全一致
    $after1 = @{}
    Get-ChildItem -LiteralPath (Join-Path $work 'qc') -Filter '*.qc' | ForEach-Object { $after1[$_.Name] = Get-FileHashText $_.FullName }
    $out2 = New-RunDir 'main_inline_2'
    $r2 = Invoke-Tool 'batch_main_2' (@('--batch', $work, '--out', $out2) + $common)
    Assert ($r2.Code -eq 0) ("第二次 batch 退出码 " + $r2.Code)
    foreach ($n in $after1.Keys) {
        Assert ((Get-FileHashText (Join-Path $work ("qc\" + $n))) -eq $after1[$n]) ("重跑后 QC 发生变化（应幂等）: " + $n)
    }
    # 备份不覆盖：.lodbak 仍是最初原件
    Assert ((Get-FileHashText (Join-Path $work 'qc\actor_main.qc.lodbak')) -eq $qcBefore['actor_main.qc']) '第二次运行覆盖了 .lodbak（应为永不覆盖）'
}

Check 'batch 模式1：独立 _LOD.qc，再切回模式0会删掉旧文件' {
    $work = Copy-Project $P.main 'main_separate'
    $out = New-RunDir 'main_separate'
    $r = Invoke-Tool 'batch_sep' (@('--batch', $work, '--out', $out, '--mode', '1') + $common)
    Assert ($r.Code -eq 0) ("batch 退出码 " + $r.Code + "`n" + $r.Out)
    $actorLod = Join-Path $work 'qc\actor_LOD.qc'
    Assert (Test-Path -LiteralPath $actorLod) '缺少 actor_LOD.qc'
    $actor = Read-TextAuto (Join-Path $work 'qc\actor.qc')
    Assert-Contains $actor.Text '$include "actor_LOD.qc"' '原 QC 应加 include 行'
    Assert-NotContains $actor.Text '$lod 20' '原 QC 不应包含 $lod 本体（应在 _LOD.qc）'
    $lodText = Read-TextAuto $actorLod
    Assert-Contains $lodText.Text '$lod 20' '_LOD.qc 应包含 $lod'
    $out2 = New-RunDir 'main_sep_to_inline'
    $r2 = Invoke-Tool 'batch_sep2inline' (@('--batch', $work, '--out', $out2, '--mode', '0') + $common)
    Assert ($r2.Code -eq 0) ("切回模式0 退出码 " + $r2.Code)
    Assert (-not (Test-Path -LiteralPath $actorLod)) '切回模式0 后应删除不再被引用的 _LOD.qc'
    $inlineActorMain = Read-TextAuto (Join-Path $work 'qc\actor_main.qc')
    Assert-Contains $inlineActorMain.Text '$lod 20' '模式0 应把 $lod 注回模型声明文件 actor_main.qc'
    $inlineRoot = Read-TextAuto (Join-Path $work 'qc\actor.qc')
    Assert-NotContains $inlineRoot.Text '//>>> L4D2 自动 LOD 工具 生成块 开始 >>>' '切回模式0 后根 QC 不应残留 include 块'
}

Check 'batch 模式2：仅合集，不改任何 QC' {
    $work = Copy-Project $P.main 'main_collection'
    $before = Get-TreeHash (Join-Path $work 'qc')
    $out = New-RunDir 'main_collection'
    $r = Invoke-Tool 'batch_collection' (@('--batch', $work, '--out', $out, '--mode', '2') + $common)
    Assert ($r.Code -eq 0) ("batch 退出码 " + $r.Code + "`n" + $r.Out)
    $after = Get-TreeHash (Join-Path $work 'qc')
    Assert ($before -eq $after) '模式2 不应修改任何 QC'
    $col = Join-Path $work '_LOD合集.qc'
    Assert (Test-Path -LiteralPath $col) '缺少 _LOD合集.qc'
    $colText = Read-TextAuto $col; Assert-Contains $colText.Text '$lod 20' '合集内容'
}

Check '手写 $lod：默认跳过；--force 注释停用并替换' {
    $work = Copy-Project $P.hand 'hand'
    $qc = Join-Path $work 'hand.qc'
    $orig = Get-FileHashText $qc
    $out = New-RunDir 'hand_default'
    $r = Invoke-Tool 'batch_hand_default' (@('--batch', $work, '--out', $out) + $common)
    $pj = Read-Json (Join-Path $out 'plan.json')
    Assert ($pj.counts.skip -eq 1) ("默认应跳过 1 个，实际 " + $pj.counts.skip)
    Assert ((Get-FileHashText $qc) -eq $orig) '默认跳过时不应修改 QC'
    $out2 = New-RunDir 'hand_force'
    $r2 = Invoke-Tool 'batch_hand_force' (@('--batch', $work, '--out', $out2, '--force') + $common)
    Assert ($r2.Code -eq 0) ("force 退出码 " + $r2.Code + "`n" + $r2.Out)
    $text = (Read-TextAuto $qc).Text
    Assert-Contains $text '// [autoLOD 已停用] $lod 40' 'force 应注释停用旧 $lod'
    Assert-Contains $text '//>>> L4D2 自动 LOD 工具 生成块 开始 >>>' 'force 应写入新工具块'
    Assert-Contains $text '$lod 20' 'force 应写入新参数'
    Assert (Test-Path -LiteralPath ($qc + '.lodbak')) 'force 修改前应有备份'
}

Check '工具标记块损坏：拒绝修改、原文件不动、退出码非 0' {
    $work = Copy-Project $P.broken 'broken'
    $qc = Join-Path $work 'broken.qc'
    $orig = Get-FileHashText $qc
    $out = New-RunDir 'broken'
    $r = Invoke-Tool 'batch_broken' (@('--batch', $work, '--out', $out) + $common)
    Assert ($r.Code -ne 0) ("损坏工程应失败，实际退出码 " + $r.Code)
    Assert-Contains $r.Out '工具标记块不完整' '错误提示'
    Assert ((Get-FileHashText $qc) -eq $orig) '损坏时不应修改 QC'
}

Check '无 QC 的散落模型：自动建包装 QC 并注入' {
    $work = Copy-Project $P.loose 'loose'
    $out = New-RunDir 'loose'
    $r = Invoke-Tool 'batch_loose' (@('--batch', $work, '--out', $out) + $common)
    Assert ($r.Code -eq 0) ("batch 退出码 " + $r.Code + "`n" + $r.Out)
    $wrapper = Join-Path $work 'props\自动LOD_crate.qc'
    Assert (Test-Path -LiteralPath $wrapper) '缺少自动创建的包装 QC'
    Assert-Contains (Read-TextAuto $wrapper).Text '$lod 20' '包装 QC 应被注入'
    Assert (Test-Path -LiteralPath (Join-Path $work 'props\crate_lod1.glb')) '缺少 crate_lod1.glb'
    Assert (-not (Test-Path -LiteralPath (Join-Path $work 'props\crate_lod3.glb'))) '只配置了 2 级，不应有 lod3'
    Assert (-not (Test-Path -LiteralPath (Join-Path $work 'props\crate_small_lod1.glb'))) '低于面数门槛的模型不应生成 LOD'
}

Check 'SMD 工程：SMD→GLB→减面→SMD，权重骨骼保留' {
    $work = Copy-Project $P.smd 'smd'
    $out = New-RunDir 'smd'
    $r = Invoke-Tool 'batch_smd' (@('--batch', $work, '--out', $out) + $common)
    Assert ($r.Code -eq 0) ("batch 退出码 " + $r.Code + "`n" + $r.Out)
    $lod1 = Join-Path $work 'smd\char_src_lod1.smd'
    $lod2 = Join-Path $work 'smd\char_src_lod2.smd'
    Assert (Test-Path -LiteralPath $lod1) '缺少 char_src_lod1.smd'
    Assert (Test-Path -LiteralPath $lod2) '缺少 char_src_lod2.smd'
    $text = (Read-TextAuto $lod1).Text
    Assert-Contains $text 'version 1' 'SMD 头'
    Assert-Contains $text 'triangles' 'SMD 三角形节'
    # 顶点行首字段是骨骼索引，应只出现 0/1
    $triStart = $text.IndexOf('triangles')
    $body = $text.Substring($triStart)
    $lines = $body -split "`n"
    $vertexLines = 0
    foreach ($ln in $lines) {
        if ($ln -match '^\s*(\d+)\s+[-0-9.]+\s+[-0-9.]+\s+[-0-9.]+\s+[-0-9.]+\s+[-0-9.]+\s+[-0-9.]+\s+[-0-9.]+\s+[-0-9.]+\s+(\d+)') {
            $bone = [int]$Matches[1]
            Assert ($bone -le 1) ("SMD 顶点骨骼索引异常: " + $bone)
            $vertexLines++
        }
    }
    Assert ($vertexLines -gt 30) ("SMD 顶点行过少: " + $vertexLines)
    # 面数应减少
    $qc = (Read-TextAuto (Join-Path $work 'smd\char.qc')).Text
    Assert-Contains $qc '$lod 20' 'SMD 工程 QC 注入'
    Assert-Contains $qc 'replacemodel "char_src.smd"' 'replacemodel 源名逐字'
}

Check '中文/空格/长路径工程' {
    $work = Copy-Project $P.paths 'paths'
    $out = New-RunDir 'paths'
    $r = Invoke-Tool 'batch_paths' (@('--batch', $work, '--out', $out) + $common)
    Assert ($r.Code -eq 0) ("batch 退出码 " + $r.Code + "`n" + $r.Out)
    Assert (Test-Path -LiteralPath (Join-Path $work 'crate_lod1.glb')) '长路径下缺少 LOD'
    $pathsQc = Read-TextAuto (Join-Path $work '中文工程.qc'); Assert-Contains $pathsQc.Text '$lod 20' '长路径 QC 注入'
}

Check '空工程：计划为空且不报错崩溃' {
    $work = Copy-Project $P.empty 'empty'
    $out = New-RunDir 'empty'
    $r = Invoke-Tool 'plan_empty' (@('--plan', $work, '--out', $out) + $common)
    $pj = Read-Json (Join-Path $out 'plan.json')
    Assert ($pj.counts.qc -eq 0) ("空工程 QC 数应为 0，实际 " + $pj.counts.qc)
    Assert ($pj.counts.jobs -eq 0) '空工程任务数应为 0'
    Assert-Contains $r.Out 'PLAN: 0 QC' '空工程提示'
}

if ($IncludeCancel) {
    Check '取消：杀掉进程后无残留 Blender' {
        $before = (Get-Process blender -ErrorAction SilentlyContinue | Measure-Object).Count
        $work = Copy-Project $P.main 'cancel'
        $out = New-RunDir 'cancel'
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = $Exe
        $psi.Arguments = '--batch "' + $work + '" --out "' + $out + '" --levels 8 --dist 10,15,20,25,30,35,40,45 --ratio 99,90,80,70,60,50,40,30'
        $psi.UseShellExecute = $false
        $psi.RedirectStandardOutput = $true
        $psi.CreateNoWindow = $true
        $p = [System.Diagnostics.Process]::Start($psi)
        Start-Sleep -Milliseconds 2500
        if (-not $p.HasExited) { $p.Kill() }
        $p.WaitForExit()
        Start-Sleep -Milliseconds 1500
        $after = (Get-Process blender -ErrorAction SilentlyContinue | Measure-Object).Count
        Assert ($after -le $before) ("杀掉主进程后残留 blender: " + $after)
    }
}

# ───────────────────────────── 汇总 ─────────────────────────────

$passed = ($Results | Where-Object { $_.Ok }).Count
$failed = ($Results | Where-Object { -not $_.Ok }).Count
$report = New-Object System.Text.StringBuilder
[void]$report.AppendLine('远景工坊 E2E 报告')
[void]$report.AppendLine('时间: ' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'))
[void]$report.AppendLine('blender: ' + $blenderExe)
[void]$report.AppendLine('exe: ' + $Exe)
[void]$report.AppendLine(('结果: {0} passed, {1} failed' -f $passed, $failed))
[void]$report.AppendLine('')
foreach ($r in $Results) {
    [void]$report.AppendLine(('[{0}] {1}  ({2} ms) {3}' -f $(if ($r.Ok) { 'PASS' } else { 'FAIL' }), $r.Name, $r.Ms, $r.Message))
}

$reportPath = Join-Path $T 'e2e_report.txt'
$report.ToString() | Set-Content -LiteralPath $reportPath -Encoding UTF8
Write-Host ''
Write-Host ('═══ E2E: {0} passed, {1} failed ═══' -f $passed, $failed) -ForegroundColor $(if ($failed -eq 0) { 'Green' } else { 'Red' })
Write-Host ('report: ' + $reportPath)
Write-Host ('logs  : ' + $LogDir)
if (-not $KeepTemp) {
    Write-Host '（-KeepTemp 未指定：临时目录保留供排查，可手动删除 ' + $T + '）'
}
exit $(if ($failed -eq 0) { 0 } else { 1 })
