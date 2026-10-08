# 不问凡尘 AI NPC 语音 MOD

当前源码版本：**v0.7.3**。为《不问凡尘》的 AI NPC 回复提供本地语音合成，支持 **IndexTTS 2.5** 与 **CosyVoice 3（audio.cpp）**，按角色选择参考音，并使用同轮模型回复给出的语音风格。

这是非官方社区 MOD，与游戏开发商、BepInEx、audio.cpp 及模型开发团队没有从属关系。需要合法安装的游戏本体；游戏 AI 对话额度仍按游戏规则使用，本 MOD 负责本地语音合成。

## 版本与下载

**源码版本与 GitHub 已发布安装包分别更新。** 当前仓库为 v0.7.3；截至 2026-10-08，GitHub [Releases](https://github.com/havoc123/A1IndexTTSMod/releases) 中的历史安装包为 v0.5.8 主包及 v0.5.9 说服语音补丁，均为预发布。它们不包含本页所述的全部新功能。本次源码同步没有发布新的 Release 安装包。

历史主包 `A1IndexTTSMod-v0.5.8-r3-win64.zip` 包含插件、audio.cpp CUDA 运行时及 1169 份 NPC 参考音；不包含游戏、基础 BepInEx 加载器或 IndexTTS 模型。已有该主包的用户可安装 `A1IndexTTSMod-v0.5.9-persuasion-patch-win64.zip`。历史安装步骤见 [INSTALL.md](INSTALL.md)，历史版本范围见 [发布说明](RELEASE-NOTES-v0.5.8.md)。

最新源码需要自行构建并准备匹配的运行时和模型。完整整合包、升级补丁、工坊包的内容以各自附带说明为准，不要将旧版安装包视为 v0.7.3 的完整环境。

## 当前功能

- AI 普通聊天和说服回复的语音接入；过滤玩家输入、系统内容与括号内动作描写。
- 按 NPC 选择专属 WAV，支持默认男/女参考音、导入 WAV、试听以及当前角色的临时或持久音色设置。
- 游戏对话框中的 **“语音设置”** 入口，提供“音色与参数”“最近语音”“本轮数据”三个页签。
- 按 NPC 名字或部分编号筛选参考音，直接显示首项，其余匹配结果可展开选择；角色姓名来自 `references/npcs/npc_id_name.csv`。
- 最近语音可重播、按当前音色重新合成；本轮数据展示可读中文、解析后的正文与风格、原始响应及最终 TTS 请求摘要。
- Mod 独立音量、自动朗读、界面缩放，以及启用和关闭的确认提示。
- 关闭语音会取消待合成任务、停止播放并关闭由本插件启动的 audio.cpp 服务；外部独立启动的服务由其原管理者控制。
- 支持 Nvidia/CUDA 与 Vulkan GPU 路由，Vulkan 可指定设备序号。
- 支持通用 Mod Manager 功能插件接口；未安装管理器时仍可独立运行。

## 语音风格如何传递

提示增强开启时，同一次游戏 LLM 回复在原生 `content` 字符串末尾输出版本化 `<a1tts_v1>` 风格帧。插件提取并独立保存合法风格，移除帧后将干净正文交给游戏与 TTS，不依赖中间代理保留额外 JSON 字段。合法的旧版顶层 `voice_style` 仍可读取。

菜单第三页区分解析视图与原始回包，显示风格来源和解析状态。协议格式、两种来源的优先规则、长度限制及失败行为见 [正文风格帧 v1](docs/CONTENT-STYLE-ENVELOPE-V1.md)。

该机制不是对所有代理的无条件兼容保证：模型漏帧、回复截断或上游修改正文，仍可能导致风格缺失。部分代理会依据正文推断游戏情绪，帧内文字可能影响该推断。插件对缺失或无效风格使用既有回退策略，不凭空恢复原模型数据。

## 运行环境与安装

- Windows x64，游戏为 Unity IL2CPP；本项目使用过的游戏构建 GUID 为 `0acccbcdb9a14aa3a528bd4d850c3202`，游戏更新后需重新确认兼容性。
- BepInEx 6 IL2CPP；本项目验证的加载器为 `6.0.0-be.788+5b766a3`。游戏启动后需能生成 BepInEx 日志与 `interop`。
- 与所选后端匹配的 audio.cpp 运行时、模型及参考音。IndexTTS 和 CosyVoice 的模型与服务设置不能互换。
- Nvidia 路线使用 CUDA 运行时；Vulkan 路线还需准备 `audiocpp_server-vulkan.exe` 及其配套文件。仅改配置不会自动下载这些文件，也不代表所有显卡已实测通过。

关闭游戏后安装。最新版插件的关键依赖结构为：

```text
BepInEx/plugins/
  LocalModManager.Abstractions.dll
  A1IndexTTSMod/
    A1IndexTTSMod.dll
    NAudio.Core.dll
    NAudio.Wasapi.dll
    NAudio.WinMM.dll
```

共享抽象 DLL 放在 `plugins` 根目录；不要在多个插件子目录重复放置不同版本。构建后的安装脚本会复制这些依赖。工坊安装器说明见 [工坊安装](packaging/INSTALL-WORKSHOP.md)，CosyVoice 整合包说明见 [CosyVoice 安装](packaging/INSTALL-COSY-COMPLETE.md)。

## 常用配置

配置由 BepInEx 生成于 `BepInEx/config/org.a1indextts.mod.cfg`。下表为 `[Stage3Mvp]` 下的主要设置：

| 设置 | 默认值 | 说明 |
| --- | --- | --- |
| `Enabled` | `true` | Mod 语音总开关 |
| `PromptEnhancement` | `true` | 为实际回复添加正文风格帧要求 |
| `Backend` | `IndexTtsAudioCpp` | 可选 `IndexTtsAudioCpp`、`IndexTtsLegacyApi`、`CosyVoiceAudioCpp` |
| `TtsUrl` | `http://127.0.0.1:8892/v1/audio/speech` | 必须与实际服务及后端匹配 |
| `GpuBackend` | `Nvidia` | `Nvidia` 使用原 CUDA 路线；`Vulkan` 使用独立 Vulkan 服务端 |
| `GpuDevice` | `0` | Vulkan 设备序号，多显卡机器按实际枚举结果选择 |
| `AudioCppPrecision` | `q8_0` | 所选精度需要对应模型文件 |

`[SpeechPanel]` 保存 Mod 播放音量、自动朗读、界面缩放和页签等偏好。播放使用 WASAPI，并提供 WinMM 回退；游戏自身的音量滑杆不会直接控制 Mod 播放音量。

`references/npcs/<npcId>.wav` 为专属参考音。姓名表中 `audio=1` 为原音、`audio=2` 为生成音、`audio=0` 为无专属音；无专属音时按角色性别使用默认参考音，未知性别按跳过策略处理。旧式情绪向量配置位于 `config/emotions.json`。

## 从源码构建

仓库 `global.json` 固定 .NET SDK `10.0.103`，插件目标框架为 .NET 6。先安装 BepInEx 并启动游戏一次，生成本机 `BepInEx/interop`，然后在仓库根目录运行：

```powershell
.\scripts\Build.ps1 -BepInExDir 'E:\你的游戏目录\BepInEx'
.\scripts\Install-Plugin.ps1 -GameRoot 'E:\你的游戏目录'
```

输出在 `src/bin/Release/net6.0/`。安装前退出游戏；安装插件不会替你下载模型和服务运行时。打包脚本依赖本机准备的运行时、模型及参考音，详见相应脚本参数和包内说明。

## 隐私、反馈与授权

完整 Stage2A 诊断默认关闭。手动开启后，记录可能包含对话与游戏上下文；分享日志和“本轮数据”前请检查并脱敏。模型、运行时缓存、游戏存档和诊断日志不作为本仓库源码同步内容。

反馈请到 [Issues](https://github.com/havoc123/A1IndexTTSMod/issues)，说明安装包/插件版本、游戏构建、GPU、TTS 后端和复现步骤，可附脱敏日志。不同游戏入口、代理和 GPU 组合的验证范围不同；编译通过不等于全部运行环境已验收。

自写源码和文档采用 [MIT](LICENSE)。游戏资料、NPC 参考音及第三方二进制不因进入仓库而自动取得 MIT 授权，来源与限制见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。参考音不作为可任意再训练、克隆或转售的开放数据集；模型与第三方运行时遵循各自许可。
