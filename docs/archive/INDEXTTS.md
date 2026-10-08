# IndexTTS 历史路线（已停止维护）

归档日期：2026-10-09。当前维护主线为 **v0.7.0 起的 CosyVoice 3 / audio.cpp 与增强情感功能**。仓库、程序集和配置文件沿用 `A1IndexTTSMod` 历史名称。

## 停止维护的原因

1. 项目围绕当句 `voice_style` 传递情绪、中文说话方式和强度。原 IndexTTS 适配主要发送固定情绪向量，没有将完整 `delivery` 作为独立表演指令传递；CosyVoice 的中文 instruction 更直接符合当前实现目标。
2. 双线路需要分别维护模型、服务配置、启动参数、打包与故障排查。后续维护集中到 CosyVoice，减少安装与配置混淆。
3. 游戏与 TTS 同机运行是部署重点，资源适配集中在当前 CosyVoice 程序包、模型包和 GPU 路由上。这里不承诺特定硬件的速度，也不以模型规模代替显存或延迟实测。

这是维护范围调整，并非断言 IndexTTS 没有情感能力或音质较差。源码保留历史适配分支便于追溯，不继续作为新装默认项，也不承诺新增功能或兼容性修复。

## 历史资料

- [旧安装与卸载说明](../../INSTALL.md)
- [v0.5.8 发布说明](../../RELEASE-NOTES-v0.5.8.md)
- [旧 IndexTTS 完整包](../../packaging/INSTALL-COMPLETE.md)
- [旧工坊包介绍](../../packaging/README-WORKSHOP.md)
- 旧参数映射：`config/emotions.json`。
- 历史打包脚本：`Build-CompleteBundle.ps1`、`Build-TestRelease.ps1` 等。保留脚本不表示对应包已更新或仍推荐安装。

历史文档中的版本、默认后端、包名和测试结果仅适用于当时环境。旧包曾包含预留的 CosyVoice 适配或仍在开发的新接口，不改变当前归档 IndexTTS 的维护决定。

## 迁移到 CosyVoice

按 [CosyVoice 分体安装说明](../../packaging/INSTALL-COSY-SPLIT.md) 准备程序与模型。安装器保留已有配置，需要核对：

```ini
[Stage3Mvp]
Backend = CosyVoiceAudioCpp
TtsUrl = http://127.0.0.1:8892/v1/audio/speech
AudioCppModelId = cosyvoice3
PromptEnhancement = true
PresetVoiceStyles = true
```

模型位置为 `A1IndexTTSMod/.cache/audiocpp/models/CosyVoice3-GGUF/cosyvoice3-q8_0.gguf`，IndexTTS 模型不能替代 CosyVoice 模型。
