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

## sherpa-onnx streaming ASR

The streaming Chinese ASR integration uses sherpa-onnx 1.13.8 and the sherpa-onnx-streaming-zipformer-zh-14M-2023-02-23 model. Both are Apache-2.0; their license texts are installed beside the runtime and model by scripts/Install-ASR.ps1. The Windows CUDA runtime archive is the official sherpa-onnx v1.13.8 CUDA 12.x / cuDNN 9.x x64 release. NVIDIA CUDA and cuDNN runtime terms remain applicable to those separately installed dependencies.

The source branch optionally builds a modified sherpa-onnx C API DLL from commit `11afbd009a7f8c08f4bcf2fc1b265d0df4670fbf`. The maintained Apache-2.0 patch and build provenance are in `native/asr`; it changes contextual pruning and finalization, with an exported revision marker. `Build-AsrNative.ps1` produces the upstream license and hash manifest, and `Install-AsrNative.ps1` installs them beside the modified DLL. Model-weight and tokenizer terms remain separate and unchanged. Existing releases are not repackaged by this change.

The installer downloads NVIDIA cuDNN 9.14.0.64 for CUDA 12 from NVIDIA's official redistribution archive and installs its DLLs app-local for the user's own setup. The NVIDIA license text is retained as `LICENSE-NVIDIA-cuDNN.txt`; NVIDIA license terms apply. This project does not bundle the archive in source releases.

The optional accurate profile downloads `sherpa-onnx-streaming-zipformer-zh-fp16-2025-06-30` from [the author's fixed revision](https://huggingface.co/csukuangfj/sherpa-onnx-streaming-zipformer-zh-fp16-2025-06-30/tree/2501d7dbcc440fab07cf94ece62833b94a903c13). The inspected model repository does not provide an explicit checkpoint license. The sherpa-onnx code license must not be taken as a license grant for those weights. They are not committed or bundled in a release by this change; redistribution requires verification of the checkpoint's own terms. The installer does not attach an Apache license to this optional profile.

The generated ASR hotwords reuse a small subset of NPC/sect names from `references/npcs/npc_id_name.csv`. Their provenance is recorded in `config/asr-hotwords.sources.json`; the game rights statement above also applies to these names.

The accurate profile also downloads `bpe.model` from [the same author's XL model at fixed revision 0128977216bda3dc2b7d70178be8e721c0e49b8b](https://huggingface.co/csukuangfj/sherpa-onnx-streaming-zipformer-zh-xlarge-fp16-2025-06-30/tree/0128977216bda3dc2b7d70178be8e721c0e49b8b). Its pieces and IDs are checked against the accurate profile's token table. This tokenizer resource is downloaded during installation, not committed or bundled; its terms are separate from this project's source license, as with the optional weights above. The managed adapter implements the vocabulary export and Chinese encoding behavior of the pinned [simple-sentencepiece v0.7](https://github.com/pkufool/simple-sentencepiece/tree/v0.7) used by sherpa-onnx, including byte fallback, without adding a Python/protobuf dependency to the game. The original model vocabulary is preserved; runtime token aliases only bridge pre-tokenized hotword paths into the pinned native API.


The 0.7.7 optional DirectML 14M package includes the pinned ONNX Runtime
DirectML 1.14.1 and Microsoft.AI.DirectML 1.15.0 runtime DLLs. Their original
LICENSE and ThirdPartyNotices files are retained beside the runtime. CUDA
optional packages retain installed NVIDIA cuDNN license texts; NVIDIA runtime
terms continue to apply. Optional distributable packages contain only the
14M checkpoint; the 160M checkpoint and BPE resource remain installed locally
or downloaded directly at the fixed revisions above. Source repositories do
not contain these runtime binaries or model weights.
