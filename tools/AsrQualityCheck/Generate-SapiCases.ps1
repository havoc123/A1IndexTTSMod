[CmdletBinding()]
param([string] $OutputDirectory = (Join-Path $PSScriptRoot '../../.state/asr-quality/accuracy-sweep'))
$ErrorActionPreference = 'Stop'
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
# Development fixtures only. References are the requested synthesis text, not
# human transcriptions; this corpus must not be presented as user-voice accuracy.
$voice = New-Object -ComObject SAPI.SpVoice
$voices = $voice.GetVoices()
$chineseVoice = $null
for ($i = 0; $i -lt $voices.Count; $i++) {
    $candidate = $voices.Item($i)
    if ($candidate.GetAttribute('Language') -eq '804') { $chineseVoice = $candidate; break }
}
if (-not $chineseVoice) { throw 'Install a Windows SAPI zh-CN voice before generating these offline fixtures.' }
$voice.Voice = $chineseVoice
$definitions = @(
    @{name='name-at-start'; group='positive'; split='calibration'; text='苏倾盏，你在这里吗？'; words=@('苏倾盏')},
    @{name='name-in-sentence'; group='positive'; split='calibration'; text='我想找苏倾盏，请问她在哪里？'; words=@('苏倾盏')},
    @{name='name-and-sect'; group='positive'; split='calibration'; text='苏倾盏是焚天宗的弟子。'; words=@('苏倾盏','焚天宗')},
    @{name='two-sects'; group='positive'; split='calibration'; text='我想去焚天宗，也想去镇岳宗。'; words=@('焚天宗','镇岳宗')},
    @{name='common-names'; group='positive'; split='calibration'; text='念乔，王小二，谢长风，你们好。'; words=@('念乔','王小二','谢长风')},
    @{name='rare-names'; group='positive'; split='calibration'; text='我想请纪小蝶和江楚弦帮忙。'; words=@('纪小蝶','江楚弦')},
    @{name='neutral-fruit'; group='negative'; split='calibration'; text='今天吃了一个苹果，我们明天去公园散步。'; words=@()},
    @{name='neutral-cup'; group='negative'; split='calibration'; text='这个杯子有点倾斜，桌子上放着一盏灯。'; words=@()},
    @{name='neutral-city'; group='negative'; split='calibration'; text='苏州今天的天气很好，青山绿水就在眼前。'; words=@()},
    @{name='heldout-invitation'; group='positive'; split='heldout'; text='请苏倾盏过来，我们一起去焚天宗。'; words=@('苏倾盏','焚天宗')},
    @{name='heldout-question'; group='positive'; split='heldout'; text='你见过苏倾盏吗？我有事找她。'; words=@('苏倾盏')},
    @{name='heldout-friends'; group='positive'; split='heldout'; text='念乔和王小二在等你，谢长风也来了。'; words=@('念乔','王小二','谢长风')},
    @{name='heldout-rare'; group='positive'; split='heldout'; text='莲心和陆浩元去了镇岳宗。'; words=@('莲心','陆浩元','镇岳宗')},
    @{name='heldout-fire'; group='negative'; split='heldout'; text='火焰正在燃烧，天空已经变亮。'; words=@()},
    @{name='heldout-poem'; group='negative'; split='heldout'; text='我念了一首诗，走过一座桥，然后坐下休息。'; words=@()},
    @{name='heldout-money'; group='negative'; split='heldout'; text='请给我两杯茶，再拿三个馒头，一共多少钱？'; words=@()},
    @{name='heldout-direction'; group='negative'; split='heldout'; text='沿着这条路向前走，到了门口再向左转。'; words=@()}
)
$manifest = @()
foreach ($definition in $definitions) {
    $path = Join-Path $OutputDirectory ($definition.name + '.wav')
    $stream = New-Object -ComObject SAPI.SpFileStream
    $stream.Format.Type = 22 # 22050 Hz, 16-bit, mono.
    $stream.Open($path, 3, $false)
    try {
        $voice.AudioOutputStream = $stream
        $voice.Speak($definition.text) | Out-Null
    } finally { $stream.Close() }
    $manifest += [pscustomobject]@{ name=$definition.name; wav=$path; reference=$definition.text; expectedWords=$definition.words; group=$definition.group; split=$definition.split }
}
$previousDirectory = [IO.Path]::GetFullPath((Join-Path $OutputDirectory '..'))
$mixed = Join-Path $previousDirectory 'fixtures/hotwords-sapi.wav'
if (Test-Path -LiteralPath $mixed) {
    $manifest += [pscustomobject]@{ name='original-mixed'; wav=$mixed; reference='苏倾盏。焚天宗。我想找苏倾盏，去焚天宗看看。念乔。今天吃了一个苹果。'; expectedWords=@('苏倾盏','焚天宗','苏倾盏','焚天宗','念乔'); group='positive'; split='calibration' }
}
$natural = Join-Path $previousDirectory 'fixtures/0.wav'
if (Test-Path -LiteralPath $natural) {
    $manifest += [pscustomobject]@{ name='official-natural'; wav=$natural; reference='对我做了介绍啊那么我想说的是呢大家如果对我的研究感兴趣呢'; expectedWords=@(); group='negative'; split='calibration' }
}
foreach ($split in @('calibration','heldout')) {
    @($manifest | Where-Object split -eq $split) | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $OutputDirectory ($split + '.json')) -Encoding utf8
}
[pscustomobject]@{ voice=$chineseVoice.GetDescription(); cases=$manifest.Count; sampleRate=22050; calibration='calibration.json'; heldout='heldout.json' } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDirectory 'provenance.json') -Encoding utf8
Write-Host "Generated $($manifest.Count) offline cases using $($chineseVoice.GetDescription())."
