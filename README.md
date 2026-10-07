# 不问凡尘 AI NPC 语音 MOD

让《不问凡尘》内置 AI 对话的 NPC 回复由本机 IndexTTS 2.5 朗读。插件从游戏显示的 NPC 正文与情绪标签取材，按 NPC ID 选择参考音，待整句合成完成后通过游戏进程内的 WASAPI 共享模式播放。玩家输入、系统提示词和括号内的动作描写不会朗读。

这是非官方社区 MOD，与游戏开发商、BepInEx、audio.cpp 和 IndexTTS 团队没有从属关系。需要合法安装的游戏本体；游戏自带的 AI 对话额度仍按游戏规则使用，本 MOD 只在本地合成语音。

## 下载与安装

从 [Releases](https://github.com/havoc123/A1IndexTTSMod/releases) 下载 `A1IndexTTSMod-v0.5.8-r3-win64.zip`；已安装此版本的用户可覆盖安装 `A1IndexTTSMod-v0.5.9-persuasion-patch-win64.zip`，增加说服小游戏 NPC 回复朗读。主包包含插件、audio.cpp Windows CUDA 运行时、**1169 份 NPC WAV 参考音**、默认男/女参考音与 ID 表；不包含游戏、BepInEx 基础加载器或约 3.5 GB 的 IndexTTS 模型；已包含路径无关的 Doorstop 启动器及首次运行缓存。首次安装需要分别安装 BepInEx 6 IL2CPP 和下载 Q8 GGUF。完整步骤、目录图、校验与卸载方法见 [INSTALL.md](INSTALL.md)。

当前版本针对 Windows x64、Unity IL2CPP 游戏构建 `0acccbcdb9a14aa3a528bd4d850c3202`、BepInEx `6.0.0-be.788` 和 NVIDIA CUDA 版 audio.cpp 测试。首次载入模型和预热需要等待；显存、驱动和性能会随显卡及模型精度变化。`f16`、`orig` 可在配置中选择，但需自行下载同名模型文件。

## 功能与配置

- 识别游戏内置 AI 普通聊天的 NPC ID、正文和九种结构化情绪标签，使用 `config/emotions.json` 映射八维情绪向量；该配置可在游戏运行中修改。
- `references/npcs/<npcId>.wav` 是专属参考音。表 `references/npcs/npc_id_name.csv` 中 `audio=1` 为原音、`audio=2` 为生成音、`audio=0` 为无专属音。`audio=0` 时按性别使用 `references/default_male.wav` 或 `default_female.wav`，未知性别不朗读。
- 自动打开可见的 audio.cpp 控制台；正常退出游戏时关闭服务。服务失败时文字对话照常进行。
- BepInEx 配置中的 `Stage3Mvp.AudioCppPrecision` 默认 `q8_0`。`Stage2A.Enabled` 和 `Stage2A.CaptureFullPrompt` 默认关闭。

安装 v0.5.9 补丁后，已验证官方 AI 普通聊天和说服小游戏的 NPC 回复；其他入口及游戏更新后的兼容性尚需实测。播放走 WASAPI，不受游戏内音量滑杆直接控制。版本范围和已知限制见 [发布说明](RELEASE-NOTES-v0.5.8.md)。

## Mod Manager 支持

当前源码集成版 `v0.6.0` 实现了通用 BepInEx 功能插件接口，可在兼容的 Mod Manager 设置页中查看并开关 IndexTTS。**该接口尚未包含在 Releases 中的 v0.5.9 包**；请勿把旧发行版与下文的管理器支持混为一谈。安装要求、目录和开关行为见 [Mod Manager 集成说明](docs/MOD-MANAGER-SUPPORT.md)。

集成使用 `LocalModManager.Abstractions.dll` 作为共享契约：它必须放在 `BepInEx/plugins/` 根目录。Mod Manager 本身也是同一个 BepInEx 加载链中的插件；不要另启管理器启动器或第二套 Doorstop/BepInEx。TTSMod 未安装管理器时仍可单独运行，但 v0.6.0 接口构建仍需要共享抽象 DLL。

给管理器作者的改动说明、可合并提交号与本机验证范围见 [作者合并说明](docs/MOD-MANAGER-AUTHOR-HANDOFF.md)。管理器 fork 的功能分支目前保留在本地，尚未推送或发起 PR。

## 隐私与反馈

默认不采集完整提示词或对话。手动开启 Stage2A 诊断时，记录会存于游戏内 `A1IndexTTSMod/.state/stage2a`，其中可能含私密聊天和游戏上下文；提交问题前请自行检查、删去敏感内容。运行时 `.state`、模型和日志不纳入 Git 或发行 ZIP。

遇到问题请在 [Issues](https://github.com/havoc123/A1IndexTTSMod/issues) 写明 MOD 版本、游戏构建、显卡和复现步骤，可附脱敏后的 BepInEx 日志。请勿上传存档、完整提示词、令牌或他人的语音。

## 从源码构建

仓库中的 `global.json` 固定 .NET SDK `10.0.103`，插件目标框架为 .NET 6。先按安装文档部署 BepInEx 并运行游戏一次以生成 IL2CPP interop，然后在 PowerShell 执行 `./scripts/Build.ps1 -BepInExDir <游戏目录>/BepInEx`。编译结果在 `src/bin/Release/net6.0/`。`scripts/Build-TestRelease.ps1` 还要求本地准备 audio.cpp CUDA 运行时、Q8 模型和全部参考音，且不会把模型放进发行 ZIP。

## 开源与素材

本项目自写的源码和文档以 [MIT 许可证](LICENSE)发布。游戏名称、角色、NPC 身份表、参考音频及第三方二进制不因进入仓库而自动获得 MIT 授权；其权利与来源见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。音频经发布者确认具有公开再分发授权，**音频不作为可任意再训练、克隆或转售的开放数据集**。IndexTTS 模型按其自身许可从上游下载。
