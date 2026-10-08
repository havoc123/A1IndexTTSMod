# CosyVoice 完整版第三方与素材声明

本包仅包含本项目自写插件、安装脚本与必要的依赖运行资产。项目 MIT 许可证不覆盖以下第三方软件、模型或录音。

- **BepInEx 6 IL2CPP `6.0.0-be.788+5b766a3`**：来自 [BepInEx 官方构建](https://builds.bepinex.dev/projects/bepinex_be/788/)。组件受 LGPL-2.1 许可；许可文本随包提供为 `third_party-BepInEx-LICENSE.txt`，源码见 [BepInEx](https://github.com/BepInEx/BepInEx)。
- **Unity 2022.3.43 基础库缓存**：`A1/BepInEx/unity-libs/2022.3.43.zip` 是 BepInEx 首次运行通常从 `unity.bepinex.dev` 获取的库缓存。Unity 组件权利归 Unity Technologies。
- **UnityDoorstop 4.5.0 A1 适配版**：随包的 `A1/winhttp.dll` 是适配本游戏加载入口的构建，SHA-256 与项目记录应为 `9e282a33d82356df13ddf50a4b6c1d034ac72b9585b82e861b5d064c79b4773b`。采用 LGPL-2.1；许可文本和相应源码可在本项目发行源中查阅。
- **audio.cpp `v0.8.2-audio8-perf-hotfix` Windows x64 CUDA 运行时**：采用 Apache-2.0；完整许可随包在 `A1/A1IndexTTSMod/.cache/audiocpp/runtime/LICENSE`。此发行构建包含 NVIDIA CUDA 组件和 Microsoft Visual C++ 运行时 DLL，各自受 [CUDA EULA](https://docs.nvidia.com/cuda/eula/index.html) 与 Microsoft 再分发条款约束。
- **社区 Vulkan 服务端**：随用户提供的 `audio-v0.8.2-audio8-perf-hotfix-bin-windows-x64-vulkan` 压缩包，仅取出 `audiocpp_server.exe` 并以 `audiocpp_server-vulkan.exe` 随包提供；SHA-256 为 `57337d738a69f9b8a182c7baf9663f466274a9a418a99131ea48bce0ccf2b3bc`。该文件未带数字签名。它共用随包现有运行目录中的 DLL；不从社区压缩包复制 DLL。混合显卡机器请在 `GpuDevice` 设置里选 AMD 对应编号。
- **CosyVoice3 Q8_0 GGUF**：GGUF 来自 [audio.cpp GGUF 仓库](https://huggingface.co/audio-cpp/audio.cpp-gguf/tree/main/CosyVoice3-GGUF)，原模型来自 [FunAudioLLM/CosyVoice](https://github.com/FunAudioLLM/CosyVoice)，原模型 Apache-2.0 许可文本随包提供为 `third_party-CosyVoice-LICENSE.txt`。GGUF 转换或量化不意味着原权利人认可衍生版本。
- **NAudio 2.3.0**：随插件提供 `NAudio.Core.dll`、`NAudio.Wasapi.dll` 和 `NAudio.WinMM.dll`，采用 MIT；文本随包提供为 `third_party-NAudio-LICENSE.txt`。
- **NPC WAV、默认参考音和游戏角色资料**：发布者已确认公开再分发授权。它们不在本项目 MIT 许可证范围内；继续使用、训练、声音克隆或再分发须遵守录音与声音权利人的授权。游戏名称和角色权利归各自权利人所有。

本包不含游戏本体、存档、官方 AI 额度、个人对话或提示词日志、令牌或用户运行状态。

## 0.7.5 分体发行范围

程序包包含加载器、运行时、插件和参考音，不含 GGUF 模型；模型包包含 CosyVoice3 Q8_0 GGUF 和安装辅助文件，不包含加载器、audio.cpp 运行时或参考音。以上组件说明按各自实际包内文件适用。
