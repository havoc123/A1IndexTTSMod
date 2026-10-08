# A1IndexTTSMod v0.7.3 CosyVoice 完整版

此 Windows x64 安装包含本 MOD 插件与依赖、BepInEx 6 IL2CPP 和随附 .NET 运行文件、适配本游戏的 Doorstop、Unity 2022.3.43 基础库缓存、audio.cpp CUDA 运行时与社区 Vulkan 服务端、CosyVoice3 Q8 GGUF、1169 份 NPC 参考音及默认参考音。包内不含游戏本体、存档、在线 AI 额度或 IndexTTS/Fish 模型。

## 系统要求

- 通过 Steam 安装的 Windows x64《不问凡尘》，游戏构建须为 `0acccbcdb9a14aa3a528bd4d850c3202`，Unity 2022.3.43f1 IL2CPP。
- 默认使用 NVIDIA CUDA 路由（兼容 CUDA 13.3 的驱动）。AMD 用户可把 `[Stage3Mvp] GpuBackend` 改为 `Vulkan`，需安装支持 Vulkan 的显卡驱动。audio.cpp CUDA 运行时已随包提供；显存是否充足取决于显卡，CosyVoice3 Q8 模型约 2.26 GB，运行还需要额外显存和系统内存。
- Windows PowerShell 5.1 或更新版本。无需另外安装 BepInEx、.NET 或 TTS 模型。

## 安装

1. 将 `A1IndexTTSMod-v0.7.3-cosy-complete-win64.7z.001`、`.002` 两卷和 `SHA256SUMS-A1IndexTTSMod-v0.7.3-cosy-complete-win64.txt` 校验文件放在同一目录。确认两卷都下载完整后，先校验散列。
2. 用 7-Zip 右键 `.7z.001`，选择“解压到当前文件夹”。只需解压 `.001`，`.002` 必须与它放在同一目录；解压后进入包目录。
3. 双击 `安装CosyVoice语音MOD.bat`，输入 `WorldApart.exe` 所在游戏根目录；也可以把游戏目录或 `WorldApart.exe` 拖到批处理文件上。
4. 检查目标路径和安装摘要，输入 `Y` 确认。之后从 Steam 启动游戏。首次启动时 BepInEx 会生成本机 IL2CPP interop 缓存，耗时可能约一分钟。

安装器只部署 MOD 需要的文件，不覆盖目标目录已有的 MOD 配置、参考音或任何模型。若已有 Doorstop/BepInEx 文件与随包经过确认的版本不一致，安装会停止；请先核对已有加载器和其他 MOD 的兼容性后再处理冲突。

## 旧安装迁移到 CosyVoice

安装器会保留已有的 `BepInEx/config/org.a1indextts.mod.cfg`，因此旧版配置不会被默认配置覆盖。完全退出游戏后，用记事本打开该文件，在 `[Stage3Mvp]` 下设置以下四项：

```ini
TtsUrl = http://127.0.0.1:8892/v1/audio/speech
Backend = CosyVoiceAudioCpp
AudioCppModelId = cosyvoice3
PromptEnhancement = true
```

保存后启动游戏。若某项不存在，可在 `[Stage3Mvp]` 段落新增。若要使用随包默认男女音或 NPC 专属参考音，`ReferenceId` 可保留 `demo`。旧配置中的 `Stage2A` 诊断采集选项也会保留；若曾主动开启诊断并且不再需要，请将 `Enabled` 与 `CaptureFullPrompt` 设为 `false`。

## 校验 SHA-256

PowerShell 中进入两卷和 `SHA256SUMS-A1-TTS-Mod-v0.7.0-cosy-complete-win64.txt` 所在目录，运行：

```powershell
Get-Content .\SHA256SUMS-A1IndexTTSMod-v0.7.3-cosy-complete-win64.txt | ForEach-Object {
  $expected, $name = $_ -split '\s+\*', 2
  $actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $name).Hash.ToLowerInvariant()
  if ($actual -ne $expected) { throw "SHA-256 不匹配：$name" }
  "OK $name"
}
```

## 运行与排错

- 默认启用 `CosyVoiceAudioCpp`，连接本地 `http://127.0.0.1:8892/v1/audio/speech`，模型 ID 为 `cosyvoice3`，Q8 精度。插件按需启动随包服务，并在游戏退出时停止自己启动的服务；若 8892 已有外部服务，插件会连接它而不会关闭它。
- `[Stage3Mvp] GpuBackend = Nvidia` 为默认值；AMD 显卡切换为 `Vulkan` 后需重启游戏。多显卡机器可用 `GpuDevice` 指定 AMD 的 Vulkan 设备序号；服务窗口出现 `Vulkan0` 才表示模型在 Vulkan GPU 上运行。
- 默认开启 `PromptEnhancement`，将开放式 `voice_style` 加入 NPC 回复提示词/schema，并同步恢复后的原始回复字段供样式指令消费。诊断采集默认关闭。
- 首次 IL2CPP 初始化后查看 `BepInEx/LogOutput.log`。audio.cpp 服务窗口显示模型加载与请求状态。没有语音时确认本地 8892 端口未被其他程序占用、显卡驱动正常，并检查 Windows 默认播放设备。
- 插件版本和程序集版本均为 `0.7.3`。本说明描述发布包的安装方式；实际游戏和 AMD Vulkan 推理应在目标机器按排障步骤验证。

本项目为非官方社区作品，不隶属于游戏开发商或所列第三方。
