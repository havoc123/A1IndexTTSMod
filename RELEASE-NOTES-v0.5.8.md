# v0.5.8 预发布

《不问凡尘》内置 AI 对话的 NPC 回复可由本地 audio.cpp + IndexTTS 2.5 朗读。此版本在 Windows x64、游戏构建 `0acccbcdb9a14aa3a528bd4d850c3202`、BepInEx `6.0.0-be.788` 和 Q8 GGUF 下验证。

## 下载

- `A1IndexTTSMod-v0.5.8-win64.zip`：插件、audio.cpp Windows CUDA 运行时、1169 份 NPC 参考音、男女默认音、配置与说明。**包含音频**；不包含游戏、BepInEx 基础加载器或模型。
- `SHA256SUMS.txt`：安装包散列。下载后请校验。
- Q8 模型请单独从 [audio.cpp GGUF 模型页](https://huggingface.co/audio-cpp/audio.cpp-gguf/tree/main/IndexTTS2.5-GGUF)取得，放入 `A1IndexTTSMod/.cache/audiocpp/models/IndexTTS2.5-GGUF/index-tts2_5-q8_0.gguf`。

详细步骤见 [INSTALL.md](https://github.com/havoc123/A1IndexTTSMod/blob/v0.5.8/INSTALL.md)。请按说明将 ZIP 内 `A1` 的**内容**合并到游戏根目录。首次运行会打开可见的 audio.cpp 控制台并预热模型。

## 已验证与已知限制

- 官方 AI 普通聊天的 NPC ID 选音、合成和 WASAPI 共享模式播放已在游戏内验证，用户确认能听到声音。
- 九种游戏情绪标签直接映射八维情绪向量；括号内动作描写不朗读。
- `audio=0` 的男女默认音路径经过代码选择和本地合成验证，尚未在游戏内碰到无专属音的 NPC 试听。
- 说服/话题等其他聊天入口、不同游戏构建和干净电脑安装尚未验证。语音走系统默认设备，不受游戏内音量滑杆直接控制。
- Stage2A 完整提示词诊断默认关闭。若主动开启，请自行保护和清理 `.state/stage2a` 中的本地对话记录。

本 MOD 为非官方社区项目。源码 MIT 许可不覆盖游戏素材、音频、第三方运行时或 IndexTTS 模型；参见包内 `THIRD_PARTY_NOTICES.md`。
