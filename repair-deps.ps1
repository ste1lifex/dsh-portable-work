# ===========================================================================
#  repair-deps.ps1 —— 只重建 pnpm 依赖链接，不启动服务
#  Git for Windows (core.symlinks=false) 会把 node_modules 里的符号链接检出成
#  普通文件，导致 @deepseek-ai\dsh 等入口失效。本脚本与 start-dsh.ps1 中的
#  首次修复逻辑一致，覆盖两处依赖：
#    1) 核心 app-npm\node_modules
#    2) web profile 的四个插件 + 本地 link: 插件（dsh-latex）的 junction
#  供 DshHub 控制台首次启动时自动调用。
#  用法:  powershell -ExecutionPolicy Bypass -File repair-deps.ps1
# ===========================================================================
param()
$ErrorActionPreference = 'Stop'

$root       = Split-Path -Parent $MyInvocation.MyCommand.Path
$app        = Join-Path $root 'app-npm'
$bin        = Join-Path $app 'node_modules\@deepseek-ai\dsh\lib\bin.js'
$pnpmCmd    = Join-Path $root 'tools\pnpm.cmd'
$storeDir   = Join-Path $root 'store'
$profileDir = Join-Path $root 'dsh-home\profiles\web'

function Write-Step($msg) { Write-Host "[repair] $msg" }

# ---- web profile 插件依赖：检查 + 重建链接（与 start-dsh.ps1 同名函数等价）----
# 与 start-dsh.ps1 的唯一差别：profile 清单缺失时返回空数组而不是抛错，
# 这样"只修能修的"，而不是让整个修复脚本失败。
function Get-MissingProfileDependencies {
    param([string]$ProfileDir)

    $manifestPath = Join-Path $ProfileDir 'package.json'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { return @() }

    $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $missing = @()
    foreach ($property in $manifest.dependencies.PSObject.Properties) {
        $packageJson = Join-Path $ProfileDir ("node_modules\{0}\package.json" -f $property.Name)
        if (-not (Test-Path -LiteralPath $packageJson -PathType Leaf)) {
            $missing += $property.Name
        }
    }
    return @($missing)
}

function Repair-ProfileLinkDependencies {
    param([string]$ProfileDir)

    $manifestPath = Join-Path $ProfileDir 'package.json'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { return }

    $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    foreach ($property in $manifest.dependencies.PSObject.Properties) {
        $specifier = [string]$property.Value
        if (-not $specifier.StartsWith('link:')) { continue }

        $packageDir = Join-Path $ProfileDir ("node_modules\{0}" -f $property.Name)
        if (Test-Path -LiteralPath (Join-Path $packageDir 'package.json') -PathType Leaf) { continue }

        $relativeTarget = $specifier.Substring('link:'.Length).Replace('/', [IO.Path]::DirectorySeparatorChar)
        $targetDir = [IO.Path]::GetFullPath((Join-Path $ProfileDir $relativeTarget))
        if (-not (Test-Path -LiteralPath (Join-Path $targetDir 'package.json') -PathType Leaf)) { continue }

        $packageParent = Split-Path -Parent $packageDir
        New-Item -ItemType Directory -Force -Path $packageParent | Out-Null
        if (Test-Path -LiteralPath $packageDir) {
            Move-Item -LiteralPath $packageDir -Destination "$packageDir.invalid-$(Get-Date -Format 'yyyyMMdd-HHmmss')"
        }
        New-Item -ItemType Junction -Path $packageDir -Target $targetDir | Out-Null
        Write-Step "已重建本地插件链接：$($property.Name)"
    }
}

function Repair-WebProfileDependencies {
    param([string]$ProfileDir)

    $manifestPath = Join-Path $ProfileDir 'package.json'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { return }

    Repair-ProfileLinkDependencies $ProfileDir
    $missing = @(Get-MissingProfileDependencies $ProfileDir)
    if ($missing.Count -eq 0) { return }

    Write-Step ("Web Profile 插件依赖缺失：{0}" -f ($missing -join ', '))
    Write-Step '正在从锁文件恢复全部插件依赖...'
    $oldCI = $env:CI
    $env:CI = 'true'
    Push-Location $ProfileDir
    try {
        $webInstall = 1
        if (Test-Path -LiteralPath $storeDir -PathType Container) {
            & $pnpmCmd install --offline --force --ignore-scripts --frozen-lockfile --store-dir $storeDir
            $webInstall = $LASTEXITCODE
            if ($webInstall -ne 0) { Write-Step '内置缓存不完整，将联网补齐缺失依赖...' }
        }
        if ($webInstall -ne 0) {
            # 联网安装允许 pnpm-workspace.yaml 中明确放行的原生模块构建。
            & $pnpmCmd install --force --frozen-lockfile --store-dir $storeDir
            $webInstall = $LASTEXITCODE
        }
    }
    finally {
        Pop-Location
        $env:CI = $oldCI
    }

    Repair-ProfileLinkDependencies $ProfileDir
    $stillMissing = @(Get-MissingProfileDependencies $ProfileDir)
    if ($webInstall -ne 0 -or $stillMissing.Count -gt 0) {
        $detail = if ($stillMissing.Count -gt 0) { $stillMissing -join ', ' } else { "pnpm 退出码 $webInstall" }
        throw "Web Profile 插件依赖恢复失败：$detail。请检查网络或 store 离线缓存后重试。"
    }
    Write-Step 'Web Profile 插件依赖恢复完成。'
}

# ---- 先判断到底缺什么：核心入口 + profile 直接依赖 ----
$coreReady = Test-Path -LiteralPath $bin -PathType Leaf
$missingPlugins = @(Get-MissingProfileDependencies $profileDir)
if ($coreReady -and $missingPlugins.Count -eq 0) {
    Write-Step '依赖链接已就绪，无需修复。'
    exit 0
}
if (-not $coreReady) { Write-Step '核心入口缺失（app-npm\node_modules\@deepseek-ai\dsh 未就绪）。' }
if ($missingPlugins.Count -gt 0) { Write-Step ("Web Profile 插件依赖缺失：{0}" -f ($missingPlugins -join ', ')) }

if (-not (Test-Path -LiteralPath $pnpmCmd -PathType Leaf)) {
    Write-Step "[错误] 缺少 pnpm（$pnpmCmd），无法修复依赖。" 
    exit 1
}

$oldCI   = $env:CI
$oldPath = $env:PATH
$env:CI  = 'true'
$env:PATH = (Join-Path $root 'node\bin') + ';' + (Join-Path $root 'tools') + ';' + $oldPath

# ---- 1) 核心依赖（app-npm）：只在其缺失时重建 ----
$code = 0
if ($coreReady) {
    Write-Step '核心依赖链接已就绪，跳过重建。'
} else {
    Write-Step '开始重建核心依赖（首次约 1-3 分钟）...'
    Push-Location $app
    try {
        $code = 1
        if (Test-Path -LiteralPath $storeDir -PathType Container) {
            Write-Step '优先使用内置缓存（store\）...'
            & $pnpmCmd install --offline --ignore-scripts --frozen-lockfile --store-dir $storeDir
            $code = $LASTEXITCODE
            if ($code -ne 0) { Write-Step '内置缓存不完整，将联网补齐缺失依赖...' }
        }
        if ($code -ne 0) {
            & $pnpmCmd install --ignore-scripts --frozen-lockfile --store-dir $storeDir
            $code = $LASTEXITCODE
        }
    }
    finally {
        Pop-Location
    }
}

# ---- 2) web profile 插件依赖（含 dsh-latex junction）----
$webOk = $true
try {
    Repair-WebProfileDependencies $profileDir
} catch {
    Write-Step "[错误] $($_.Exception.Message)"
    $webOk = $false
}

$env:CI   = $oldCI
$env:PATH = $oldPath

if ($code -ne 0 -or -not (Test-Path -LiteralPath $bin -PathType Leaf) -or -not $webOk) {
    $webText = if ($webOk) { '成功' } else { '失败' }
    Write-Step "[错误] 依赖修复失败（核心 pnpm 退出码 $code，profile 修复 $webText）。"
    Write-Step '       若当前离线，请在有网络的电脑上先完成一次启动，或恢复 store\ 缓存。'
    exit 1
}

Write-Step '依赖修复完成：核心入口与 Web Profile 插件均已就绪。'
exit 0
