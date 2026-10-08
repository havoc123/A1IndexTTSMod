# A1 IndexTTS NPC 语音 MOD v0.7.3

为《不问凡尘》的 NPC AI 回复添加本地 IndexTTS2.5 语音。包内包含插件、audio.cpp Windows CUDA 运行时和社区 Vulkan 服务端、1169 份 NPC 参考音；不包含 BepInEx 加载器与约 3.26 GiB 的 Q8 模型。默认使用 NVIDIA CUDA；AMD 可通过 `Stage3Mvp.GpuBackend = Vulkan` 显式切换。

v0.7.0 增加当句 `voice_style` 情感与表演描述，增强实际对话的提示词和 JSON schema，并将游戏映射时丢弃的样式同步回 `ChatMessage.NpcRawOutput`。默认仍使用 IndexTTS2.5；CosyVoice 指令后端需另行准备兼容运行时与模型，并明确修改后端配置。

订阅 Steam 创意工坊条目后，请先阅读 [INSTALL.md](../INSTALL.md)。可运行同目录的 `安装TTSMod.bat` 安装，也可将 `A1` 文件夹内的内容手动合并到游戏目录。

本 MOD 可独立运行，也支持实现 `IManagedFeaturePlugin` v1 接口的 MOD 管理器。独立运行仍需 BepInEx 6 IL2CPP 和本包附带的 `LocalModManager.Abstractions.dll`。

源码与第三方许可见 [项目仓库](https://github.com/havoc123/A1IndexTTSMod) 和包内 `THIRD_PARTY_NOTICES.md`。
