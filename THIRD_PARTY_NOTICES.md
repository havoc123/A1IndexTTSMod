# 权利与第三方组件声明

本仓库的 `LICENSE` 仅适用于本项目自写的源码、脚本和文档。下列组件和内容有各自的权利归属：

| 内容 | 发布方式与适用条款 |
| --- | --- |
| 《不问凡尘》及 NPC 名称、ID、设定 | 游戏权利人保留权利。本 MOD 非官方作品；用户需自行取得并安装游戏。`npc_id_name.csv` 只用于把游戏内 NPC ID 与参考音对应起来，不按项目 MIT 许可证重新授权游戏素材。 |
| `references/npcs/*.wav`、`references/demo.wav`、男女默认 WAV | 发布者确认拥有公开再分发授权。CSV 中 `audio=1` 表示原音，`audio=2` 表示生成参考音。这些录音不在本项目 MIT 许可证范围内；再训练、声音克隆、转售或独立再分发需遵守录音及声音权利人的授权。 |
| [audio.cpp](https://github.com/0xShug0/audio.cpp) `v0.8.2-audio8-perf-hotfix` | 发行包包含其 Windows CUDA 运行时；项目代码为 Apache-2.0。完整 Apache-2.0 许可证随运行时保存在 `A1IndexTTSMod/.cache/audiocpp/runtime/LICENSE`。 |
| [NAudio](https://github.com/naudio/NAudio) `2.3.0` | 插件包附带 `NAudio.Core.dll` 与 `NAudio.Wasapi.dll`，上游采用 MIT 许可证。版权和完整文本见随包 `third_party/NAudio-LICENSE.txt` 及[上游许可证](https://github.com/naudio/NAudio/blob/main/LICENSE)。 |
| [BepInEx](https://github.com/BepInEx/BepInEx) `6.0.0-be.788` | 基础加载器不在 MOD 包内；请从官方构建获取，并遵守其上游许可。 |
| [IndexTTS 2.5](https://huggingface.co/IndexTeam/IndexTTS-2.5) / [GGUF 转换权重](https://huggingface.co/audio-cpp/audio.cpp-gguf/tree/main/IndexTTS2.5-GGUF) | 模型权重不在 Git 仓库或 Release ZIP 中。下载与使用受模型提供方的独立许可约束，GGUF 转换不会改变原权重许可。 |
| NVIDIA CUDA 与 Microsoft Visual C++ 运行时 DLL | 随所选 audio.cpp Windows CUDA 运行时使用；各自受 [NVIDIA CUDA EULA](https://docs.nvidia.com/cuda/eula/index.html) 和 Microsoft 的再分发条款约束，不受本项目 MIT 许可证覆盖。 |

本项目只提供游戏 AI 回复的本地朗读功能，不提供游戏本体、存档、官方 AI 额度或在线 TTS 服务。任何第三方商标均属其各自所有者。
