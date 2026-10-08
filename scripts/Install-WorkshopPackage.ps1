[CmdletBinding()]
param([string] $GamePath = '', [switch] $AllowMissingModel)

$ErrorActionPreference = 'Stop'

function Resolve-GameRoot([string] $InputPath) {
    $value = $InputPath.Trim().Trim('"')
    if (-not $value) { throw '未提供游戏路径。' }
    $path = [IO.Path]::GetFullPath($value)
    if (Test-Path -LiteralPath $path -PathType Leaf) {
        if ([IO.Path]::GetFileName($path) -ne 'WorldApart.exe') {
            throw "请选择 WorldApart.exe，而不是其他文件：$path"
        }
        $path = Split-Path -Parent $path
    }
    if (-not (Test-Path -LiteralPath (Join-Path $path 'WorldApart.exe') -PathType Leaf)) {
        throw "未找到 WorldApart.exe：$path"
    }
    return $path
}

try {
    $packageRoot = Split-Path -Parent $PSScriptRoot
    # The distributed script is placed beside the A1 payload. The source copy lives in scripts/.
    if (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'A1') -PathType Container) {
        $packageRoot = $PSScriptRoot
    }
    $payload = Join-Path $packageRoot 'A1'
    $required = @(
        'BepInEx\plugins\A1IndexTTSMod\A1IndexTTSMod.dll',
        'BepInEx\plugins\A1IndexTTSMod\NAudio.Core.dll',
        'BepInEx\plugins\A1IndexTTSMod\NAudio.Wasapi.dll',
        'BepInEx\plugins\A1IndexTTSMod\NAudio.WinMM.dll',
        'BepInEx\plugins\LocalModManager.Abstractions.dll',
        'A1IndexTTSMod\.cache\audiocpp\runtime\audiocpp_server.exe',
        'A1IndexTTSMod\.cache\audiocpp\runtime\audiocpp_server-vulkan.exe',
        'A1IndexTTSMod\references\demo.wav',
        'A1IndexTTSMod\scripts\Run-AudioCppForGame.ps1'
    )
    foreach ($relative in $required) {
        if (-not (Test-Path -LiteralPath (Join-Path $payload $relative) -PathType Leaf)) {
            throw "安装包不完整，缺少：$relative"
        }
    }
    $cosyModelRelative = 'A1IndexTTSMod\.cache\audiocpp\models\CosyVoice3-GGUF\cosyvoice3-q8_0.gguf'
    $indexModelRelative = 'A1IndexTTSMod\.cache\audiocpp\models\IndexTTS2.5-GGUF\index-tts2_5-q8_0.gguf'
    $modelRelative = if (Test-Path -LiteralPath (Join-Path $payload $cosyModelRelative) -PathType Leaf) { $cosyModelRelative } else { $indexModelRelative }
    if (-not $AllowMissingModel -and -not (Test-Path -LiteralPath (Join-Path $payload $modelRelative) -PathType Leaf)) {
        throw '安装包没有找到 CosyVoice3 或 IndexTTS Q8 模型文件。'
    }

    if (-not $GamePath) {
        Write-Host '请输入 WorldApart.exe 的完整路径，或其所在文件夹路径：'
        $GamePath = Read-Host '游戏路径'
    }
    $gameRoot = Resolve-GameRoot $GamePath
    $loaderFiles = @('BepInEx', 'dotnet', 'doorstop_config.ini', '.doorstop_version', 'winhttp.dll')
    $hasLoaderPayload = (Test-Path -LiteralPath (Join-Path $payload 'dotnet\coreclr.dll') -PathType Leaf) -and
        (Test-Path -LiteralPath (Join-Path $payload 'winhttp.dll') -PathType Leaf)
    if (-not $hasLoaderPayload -and -not (Test-Path -LiteralPath (Join-Path $gameRoot 'BepInEx\core\BepInEx.Unity.IL2CPP.dll') -PathType Leaf)) {
        throw '安装包或游戏目录中没有完整的 BepInEx 6 IL2CPP 加载器。'
    }
    if (Get-Process -Name 'WorldApart' -ErrorAction SilentlyContinue) {
        throw '游戏正在运行。请先退出游戏，再重新执行安装。'
    }

    $sharedRelative = 'BepInEx\plugins\LocalModManager.Abstractions.dll'
    $sharedSource = Join-Path $payload $sharedRelative
    $sharedTarget = Join-Path $gameRoot $sharedRelative
    if (Test-Path -LiteralPath $sharedTarget -PathType Leaf) {
        $sourceHash = (Get-FileHash -LiteralPath $sharedSource -Algorithm SHA256).Hash
        $targetHash = (Get-FileHash -LiteralPath $sharedTarget -Algorithm SHA256).Hash
        if ($sourceHash -ne $targetHash) {
            throw '现有 LocalModManager.Abstractions.dll 与本包版本不同，已停止安装。请先确认管理器与 TTSMod 使用同一接口版本。'
        }
    }

    if ($hasLoaderPayload) {
        foreach ($relative in $loaderFiles) {
            $source = Join-Path $payload $relative
            $target = Join-Path $gameRoot $relative
            if (-not (Test-Path -LiteralPath $source)) { continue }
            if (Test-Path -LiteralPath $target) {
                if ((Get-Item -LiteralPath $source).PSIsContainer) {
                    $sourceFiles = @(Get-ChildItem -LiteralPath $source -File -Recurse)
                    foreach ($sourceFile in $sourceFiles) {
                        $fileRelative = $sourceFile.FullName.Substring($source.TrimEnd('\').Length + 1)
                        # Plugin and configuration files are handled below, not loader binaries.
                        if ($relative -eq 'BepInEx' -and ($fileRelative -like 'plugins\*' -or $fileRelative -like 'config\*')) { continue }
                        $targetFile = Join-Path $target $fileRelative
                        if (Test-Path -LiteralPath $targetFile -PathType Leaf) {
                            $sourceHash = (Get-FileHash -LiteralPath $sourceFile.FullName -Algorithm SHA256).Hash
                            $targetHash = (Get-FileHash -LiteralPath $targetFile -Algorithm SHA256).Hash
                            if ($sourceHash -ne $targetHash) { throw "现有加载器文件版本不同，已停止覆盖：$targetFile" }
                        }
                    }
                } elseif ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash) {
                    throw "现有 Doorstop 文件版本不同，已停止覆盖：$target"
                }
            }
        }
    }

    Write-Host "目标游戏目录：$gameRoot"
    Write-Host '将复制包内的加载器、运行依赖、插件和参考音。已有配置、参考音和模型会保留。'
    if ((Read-Host '确认安装？输入 Y 继续').Trim().ToUpperInvariant() -ne 'Y') {
        Write-Host '已取消。'
        exit 0
    }

    $files = @(Get-ChildItem -LiteralPath $payload -File -Recurse -Force)
    $payloadPrefix = $payload.TrimEnd('\') + '\'
    foreach ($file in $files) {
        $relative = $file.FullName.Substring($payloadPrefix.Length)
        $target = Join-Path $gameRoot $relative
        if ($relative -ieq $sharedRelative -and (Test-Path -LiteralPath $target -PathType Leaf)) { continue }
        if ($relative -ieq 'A1IndexTTSMod\config\emotions.json' -and (Test-Path -LiteralPath $target -PathType Leaf)) { continue }
        if ($relative -ieq 'BepInEx\config\org.a1indextts.mod.cfg' -and (Test-Path -LiteralPath $target -PathType Leaf)) { continue }
        if ($relative -like 'A1IndexTTSMod\references\*' -and (Test-Path -LiteralPath $target -PathType Leaf)) { continue }
        if ($relative -like 'A1IndexTTSMod\.cache\audiocpp\models\*' -and (Test-Path -LiteralPath $target -PathType Leaf)) { continue }
        $targetDir = Split-Path -Parent $target
        New-Item -ItemType Directory -Path $targetDir -Force | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $target -Force
    }

    Write-Host '语音 MOD 文件安装完成。若已有旧配置，安装器已保留原文件，请按包内说明核对后端。'
    Write-Host '若已有参考音或模型文件，安装器会保留原文件，不覆盖。'
    if ($AllowMissingModel -and -not (Test-Path -LiteralPath (Join-Path $gameRoot $cosyModelRelative) -PathType Leaf)) {
        Write-Host '尚未安装 CosyVoice3 模型。请先安装独立模型包，再从 Steam 启动游戏。' -ForegroundColor Yellow
    } else {
        Write-Host '请从 Steam 正常启动游戏。'
    }
} catch {
    [Console]::Error.WriteLine("安装失败：$($_.Exception.Message)")
    exit 1
}
