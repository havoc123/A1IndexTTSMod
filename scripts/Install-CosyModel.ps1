[CmdletBinding()]
param([string] $GamePath = '')

$ErrorActionPreference = 'Stop'
try {
    $relative = 'A1IndexTTSMod\.cache\audiocpp\models\CosyVoice3-GGUF\cosyvoice3-q8_0.gguf'
    $source = Join-Path $PSScriptRoot "A1\$relative"
    $hashFile = Join-Path $PSScriptRoot 'MODEL-SHA256.txt'
    if (-not (Test-Path -LiteralPath $source -PathType Leaf) -or -not (Test-Path -LiteralPath $hashFile -PathType Leaf)) { throw '模型包不完整，请重新解压整个模型包。' }
    $expected = (Get-Content -LiteralPath $hashFile -Raw).Trim()
    if ($expected -notmatch '^[a-fA-F0-9]{64}$' -or (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ine $expected) { throw '模型文件校验失败，请重新下载模型包。' }
    if (-not $GamePath) { $GamePath = Read-Host '请输入 WorldApart.exe 的完整路径，或其所在文件夹路径' }
    $root = [IO.Path]::GetFullPath($GamePath.Trim().Trim('"'))
    if (Test-Path -LiteralPath $root -PathType Leaf) {
        if ([IO.Path]::GetFileName($root) -ine 'WorldApart.exe') { throw '请选择 WorldApart.exe。' }
        $root = Split-Path -Parent $root
    }
    if (-not (Test-Path -LiteralPath (Join-Path $root 'WorldApart.exe') -PathType Leaf)) { throw "未找到 WorldApart.exe：$root" }
    if (Get-Process -Name 'WorldApart' -ErrorAction SilentlyContinue) { throw '请退出游戏后再安装模型。' }
    $target = Join-Path $root $relative
    if (Test-Path -LiteralPath $target -PathType Leaf) {
        if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ieq $expected) {
            Write-Host '已经安装相同模型，无需重复安装。'
            exit 0
        }
        throw '目标位置已有不同内容的模型，已停止覆盖。请先备份并移走原文件，再安装。'
    }
    Write-Host "模型安装目录：$root"
    if ((Read-Host '确认安装 CosyVoice3 Q8_0 模型？输入 Y 继续').Trim().ToUpperInvariant() -ne 'Y') { Write-Host '已取消。'; exit 0 }
    $directory = Split-Path -Parent $target
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    # A unique temporary file prevents a partial copy from looking like an installed model.
    $temporary = Join-Path $directory ('.install-' + [Guid]::NewGuid().ToString('N') + '.tmp')
    try {
        Copy-Item -LiteralPath $source -Destination $temporary
        if ((Get-FileHash -LiteralPath $temporary -Algorithm SHA256).Hash -ine $expected) { throw '复制后的模型校验失败。' }
        Move-Item -LiteralPath $temporary -Destination $target
    } finally {
        if (Test-Path -LiteralPath $temporary -PathType Leaf) { Remove-Item -LiteralPath $temporary -Force }
    }
    Write-Host '模型安装完成。请确认程序包也已安装，再从 Steam 正常启动游戏。'
} catch {
    [Console]::Error.WriteLine("模型安装失败：$($_.Exception.Message)")
    exit 1
}
