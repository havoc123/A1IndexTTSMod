# A1IndexTTSMod 0.7.6 CosyVoice 分体完整版

本次发行将程序、依赖与参考音放在程序包，GGUF 模型放在独立模型包。两包合起来可供首次安装使用；模型内容相同的老用户只需安装程序包。

## 下载内容

| 文件 | 内容 |
| --- | --- |
| `A1IndexTTSMod-v0.7.6-cosy-program-win64.7z` | 0.7.6 插件、NAudio、BepInEx 6 IL2CPP、.NET、Unity 库缓存、A1 适配 Doorstop、audio.cpp CUDA/Vulkan 服务端及依赖、启动/停止脚本、默认参考音和 1169 条 NPC WAV、NPC 名称表、默认配置与许可 |
| `CosyVoice3-q8_0-model-win64.7z` | CosyVoice3 Q8_0 GGUF、模型安装器、模型校验值与许可 |
| `SHA256SUMS-A1IndexTTSMod-v0.7.6-cosy-split-win64.txt` | 两个压缩包的 SHA-256 校验值 |

这是两个独立的 7z 压缩包，不是分卷。请分别解压到两个文件夹，不要只解压或复制其中的安装器。安装器应与各自的 `A1` 文件夹放在一起。

## 首次安装

1. 退出游戏，等待 Steam 完成存档同步。
2. 解压程序包，双击 `安装CosyVoice语音MOD.bat`。输入 `WorldApart.exe` 的完整路径或其所在文件夹路径，确认安装。
3. 解压模型包，双击 `安装CosyVoice模型.bat`。选择同一个游戏目录，确认安装。安装器会校验模型再复制，并检查复制结果。
4. 两包均安装完成后，从 Steam 正常启动游戏。首次生成 BepInEx 互操作程序集可能需要等待。
5. 在游戏 MOD 设置中确认启用本插件，在语音设置中确认启用 Mod 语音功能，然后与 NPC 对话。

最终模型位置必须是：

```text
游戏目录/A1IndexTTSMod/.cache/audiocpp/models/CosyVoice3-GGUF/cosyvoice3-q8_0.gguf
```

程序包不包含 GGUF。只安装程序包而没有模型时，TTS 无法启动。已有相同模型可以复用；独立模型安装器会校验已有文件，相同则跳过，不同则停止覆盖。

## 已安装旧版

程序安装器保留已有用户配置、参考音和模型，更新插件与运行脚本。现有加载器或共享接口与本包不同会停止安装并指出文件，请先处理版本差异，不要直接强行覆盖其他 MOD 的依赖。

已有配置会保留。CosyVoice 后端应核对：

```ini
[Stage3Mvp]
Backend = CosyVoiceAudioCpp
TtsUrl = http://127.0.0.1:8892/v1/audio/speech
AudioCppModelId = cosyvoice3
PromptEnhancement = true
PresetVoiceStyles = true
AutoStartAudioCpp = true
AudioCppPrecision = q8_0
```

0.7.7 新装默认 `GpuBackend = Auto`，NVIDIA 自动使用 CUDA，AMD 自动使用 Vulkan，并从服务器实际设备列表选择编号。已有可行的手动配置保留；AMD 单显卡的旧 Nvidia 默认会自动解析为 Vulkan。Intel 的 TTS 继续使用手动 Vulkan 配置。实际速度依设备而异。

本包针对项目使用的 A1 Windows x64 IL2CPP 游戏及 Unity 2022.3.43 加载环境。它不含游戏本体、存档、第三方人物 MOD 或 AI 服务账号。

## 0.7.6 更新内容

默认内置预设问候语和话题开场白情感库，覆盖 1232 个角色、4180 条来源与分支记录。按当前角色和实际台词精确匹配，纯文本预设台词也可进入朗读流程；当轮有效模型风格优先。第三页明确显示“离线预设库”来源和匹配记录。新装默认后端统一为 CosyVoice，IndexTTS 路线归档。

原 0.7.5 程序包不含上述开场白功能。0.7.6 继续使用同一份 CosyVoice3 Q8_0 模型，已安装模型无需重新下载；发行目录复用的模型压缩包可能附带旧版安装说明，模型文件与安装方式不变，以本程序包说明为准。

继承 0.7.5 的输出契约增强：要求有可朗读台词的每轮回复都携带风格帧，明确帧位于 `content` 字符串内部，以及内层双引号按外层 JSON 转义。无明显情绪时也要求平静自然的风格。菜单第三页会显示本轮是否要求风格、是否检测到风格。该契约不能从技术上保证所有上游模型都会遵守。

## HTTP 500 合成失败

若最近语音提示 `500 (Internal Server Error)`，说明 TTS HTTP 服务返回了失败状态；不能仅据此判断是模型、显存、参考音还是指令推理异常。若同时显示“voice_style 已捕获”，则本轮已经解析出风格。

排查时保留失败时刻的“复制本轮诊断摘要”和 `BepInEx/LogOutput.log`。默认由游戏启动的服务使用可见控制台，请截取或复制 audio.cpp 窗口中出错时的内容；不要只截游戏面板。若以隐藏方式启动服务，再收集同一时刻的 `A1IndexTTSMod/.state/audiocpp/server.stderr.log`、`server.stdout.log`，并核对文件时间，旧日志不能代表当前故障。当前客户端只显示通用 HTTP 状态，服务端错误正文或日志才有进一步定位所需的信息。分享日志前请移除账号、令牌和私人对话。

## 校验下载

在 PowerShell 中执行以下命令，将结果与随发行提供的校验文件比较：

```powershell
Get-FileHash -Algorithm SHA256 .\A1IndexTTSMod-v0.7.6-cosy-program-win64.7z
Get-FileHash -Algorithm SHA256 .\CosyVoice3-q8_0-model-win64.7z
```

两包内都提供许可说明。程序包包含运行组件和录音；模型包仅包含模型及其安装辅助文件。请按各组件许可和素材授权使用。

## 0.7.7 ASR 可选包与旧版升级

已安装 0.7.6 分体语音包时，可使用 0.7.6 → 0.7.7 主程序补丁，配置、参考音与 CosyVoice GGUF 保留。语音输入单独安装 CUDA 14M 或 DirectML 14M 资源包。AMD/Intel 的 ASR 使用 DirectML 并固定 14M；NVIDIA CUDA 可沿用已安装的 160M，或使用 CUDA 包附带的固定版本下载脚本。安装和升级时先关闭游戏，完成后重新启动以自动检测和预热。

未安装 ASR 时不显示麦克风和第四页；完整安装但关闭语音输入时，保留第四页以便重新启用。旧 v1/v2 ASR 原生库需要一并更新到 v3。详情见 [实现、资源占用与离线结果](../docs/ASR-MODULAR-DIRECTML-20261009.md)。
