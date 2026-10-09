# 不问凡尘 NPC 语音 MOD · CosyVoice

为《不问凡尘》的 NPC 对话添加本地语音，按角色选择参考音，并结合当轮台词的情绪和说话方式朗读。

**v0.7.0 起，项目主线为 CosyVoice 3 / audio.cpp，包含增强情感功能。当前源码版本为 v0.7.7。IndexTTS 路线已停止维护，作为历史归档保留。** 仓库、插件与配置文件沿用 `A1IndexTTSMod` 名称，名称不代表当前仍以 IndexTTS 为主线。

## 下载与安装

安装包以 [GitHub Releases](https://github.com/havoc123/A1IndexTTSMod/releases) 和具体发行说明为准。**源码提交不会自动更新已下载的安装包。** 历史 v0.5.x IndexTTS 包不适用于当前 CosyVoice 安装步骤。

当前采用程序与模型分开安装：

| 下载内容 | 包含什么 | 如何安装 |
| --- | --- | --- |
| CosyVoice 程序包 | 插件、加载器、audio.cpp 运行依赖、NPC 参考音和安装器 | 解压后运行 `安装CosyVoice语音MOD.bat` |
| CosyVoice3 Q8_0 模型包 | GGUF 模型、模型安装器和校验信息 | 解压后运行 `安装CosyVoice模型.bat` |

首次安装需要两包；已有相同模型可复用。安装前退出游戏并等待 Steam 云存档同步完成，两个安装器选择同一个 `WorldApart.exe` 所在目录，安装后从 Steam 启动游戏。

完整步骤、文件名和故障排查见 **[CosyVoice 分体安装说明](packaging/INSTALL-COSY-SPLIT.md)**。早期 CosyVoice 分卷完整包见 [旧完整包说明](packaging/INSTALL-COSY-COMPLETE.md)。

v0.7.6 程序包包含默认启用的预设开场白情感库。原 v0.7.5 程序包不包含这项功能；升级程序即可，已有 CosyVoice3 Q8_0 模型无需重新下载。

### 运行要求

- 合法安装的《不问凡尘》Windows x64 版，以及可工作的 BepInEx 6 IL2CPP 加载环境。
- 与程序包匹配的 audio.cpp 服务端、CosyVoice3 Q8_0 模型和参考音。
- 默认 NVIDIA/CUDA；Vulkan 路线需相应服务端和驱动，可指定 GPU 设备。实际速度和显存需求取决于硬件。

MOD 负责本地合成；游戏 AI 对话额度、模型服务和账号仍按游戏自身设置使用。程序包不包含游戏本体或存档。

## 主要功能

- **角色音色：** 普通聊天和说服回复按 NPC 选择参考音；支持导入 WAV、试听，以及为当前角色临时应用或保存音色。
- **增强情感：** 同一次游戏模型回复给出当句情绪、说话方式和强度，传给 CosyVoice 的中文指令。
- **预设开场白：** 默认内置情感库，覆盖 1278 个角色、4311 条来源与分支记录，含 131 条说服开场白，按当前角色和实际台词匹配。
- **游戏内菜单：** 对话框旁的“语音设置”提供音色、最近语音、本轮数据；安装 ASR 可选包后另有“语音输入”第四页；NVIDIA 可选择 14M / 160M，DirectML 固定 14M。参考音可按姓名或部分编号筛选。
- **可选流式语音输入：** 发送键左侧麦克风读取系统默认输入设备，边说边更新草稿；再点麦克风停止并保留草稿，直接点发送则等待整段复核后发送一次。启用后随游戏启动预热并一直常驻，切后台不卸载。NVIDIA 使用 sherpa-onnx CUDA，AMD/Intel DirectX 12 使用 DirectML 14M，包含按模型词表预检的游戏热词；未安装可选包时隐藏麦克风和第四页。[安装与验证](docs/ASR-MODULAR-DIRECTML-20261009.md)。
- **回放与诊断：** 重播、按当前音色重合成，查看中文情感、来源、实际响应与 TTS 请求摘要。
- **播放控制：** 独立音量、自动朗读和界面缩放；启用与关闭均需确认。关闭会取消任务、停止播放，并关闭插件自行启动的 audio.cpp 服务。
- **独立运行：** 无需 MOD 管理器，也可接入支持的通用功能插件接口。

## 情感从哪里来

### 模型生成的对话

插件增强游戏原有的实际回复请求，要求有可朗读台词时，在原生 `content` 字符串末尾携带语音风格帧。插件提取情感并移除帧，游戏显示与 TTS 朗读干净正文，情感通过独立 `instruction` 交给 CosyVoice。

增强利用这次游戏对话已有的人物卡、关系与上下文，不为每句情感再调用额外模型。模型漏帧、截断或代理改写正文仍可能造成风格缺失；第三页显示解析状态，不把缺失一律判定为 MOD 冲突。详见 [正文风格帧 v1](docs/CONTENT-STYLE-ENVELOPE-V1.md)。

### 游戏预设的问候与开场白

预设台词不一定经过上述模型请求，因此另有根据角色资料、台词与话题背景离线编写的情感库。纯文本 NPC 消息精确匹配后也能朗读；当轮已有有效模型风格时优先使用模型风格。

第三页明确标注“离线预设库”，不会把它当成模型回包。4311 条记录含重复文本、条件模板和分支，并非 4311 句不同台词。情感库不新增台词，也不改变开场白出现条件。详见 [预设情感库说明](docs/PRESET-VOICE-STYLES.md)。

## 配置

配置位于 `BepInEx/config/org.a1indextts.mod.cfg`。当前源码新装默认使用 CosyVoice；升级保留已有配置，从 IndexTTS 迁移时需安装 CosyVoice 模型并核对：

```ini
[Stage3Mvp]
Enabled = true
Backend = CosyVoiceAudioCpp
TtsUrl = http://127.0.0.1:8892/v1/audio/speech
AudioCppModelId = cosyvoice3
PromptEnhancement = true
PresetVoiceStyles = true
AutoStartAudioCpp = true
AudioCppPrecision = q8_0
GpuBackend = Auto
GpuDevice = 0
```

`[SpeechPanel]` 保存音量、自动朗读、界面缩放和上次页签。日常音色设置直接在游戏菜单操作。模型与运行时不能仅靠修改后端名称完成切换。

## 从源码构建

使用 `global.json` 指定的 .NET SDK，插件目标框架为 .NET 6。准备本机游戏的 BepInEx 与已生成的 `interop` 后运行：

```powershell
.\scripts\Build.ps1 -BepInExDir 'E:\你的游戏目录\BepInEx'
.\scripts\Install-Plugin.ps1 -GameRoot 'E:\你的游戏目录'
```

输出在 `src/bin/Release/net6.0/`。情感库编译进 DLL，无需单独安装 JSON。安装前退出游戏；安装脚本同步插件依赖和启停脚本，不负责下载模型。

## 版本与历史路线

| 版本 | 主线变化 |
| --- | --- |
| v0.7.0 | CosyVoice 主线，加入当句情感与说话方式增强 |
| v0.7.1–v0.7.3 | 游戏内语音菜单、音色管理、播放完善和 GPU 路由 |
| v0.7.4 | 修复自定义模型无 Schema 时的风格传输 |
| v0.7.5 | 有可朗读台词时必填风格契约；程序与模型分体安装 |
| v0.7.6 | 默认内置预设开场白情感库，新装默认后端统一为 CosyVoice |
| 历史 IndexTTS 路线 | 已停止维护，仅供历史安装和代码参考 |

**为什么停止维护 IndexTTS？** 项目需要传递“情绪 + 中文说话方式”的完整演绎描述。原 IndexTTS 适配主要发送固定情绪向量，没有直接承接完整说话方式；CosyVoice 的独立中文指令更符合当前数据链。集中维护一套模型、启动配置与安装方案，也能减少双线路造成的部署混淆和维护成本。这个取舍不表示 IndexTTS 没有情感能力，也不代表做过统一性能或音质排名。

历史源码和资料保留，已有后端分支不作为当前维护承诺。见 **[IndexTTS 历史路线](docs/archive/INDEXTTS.md)**；详细更新见 [CHANGELOG](CHANGELOG.md)。

## 反馈与许可

反馈请附插件版本、安装包名称、GPU、复现步骤和对应时刻的“复制本轮诊断摘要”。HTTP 500 表示 TTS 服务返回失败，需要同一时刻的 audio.cpp 服务端错误信息才能继续定位。

[提交问题](https://github.com/havoc123/A1IndexTTSMod/issues) · [MIT 许可](LICENSE) · [第三方说明](THIRD_PARTY_NOTICES.md)

非官方社区 MOD，与游戏开发商、BepInEx、audio.cpp 或模型团队无从属关系。分享日志前请检查私人对话和凭据；完整诊断采集默认关闭。
