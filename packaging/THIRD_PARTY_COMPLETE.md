# 整合版第三方与素材声明

本整合版直接附带 BepInEx 6 IL2CPP 加载器和 IndexTTS 2.5 Q8 GGUF 模型。包内软件与素材分别受原始许可约束；本项目 MIT 许可证仅覆盖自写的源码、脚本和文档。

- **IndexTTS 2.5 Q8 GGUF**：来自 [audio.cpp GGUF 仓库](https://huggingface.co/audio-cpp/audio.cpp-gguf/tree/main/IndexTTS2.5-GGUF)，原权重来自 [IndexTeam/IndexTTS-2.5](https://huggingface.co/IndexTeam/IndexTTS-2.5)，适用 bilibili Model Use License。协议全文随包在 `IndexTTS-2.5-LICENSE.txt`；接收者使用或继续分发时须遵守该协议。GGUF 量化不意味着权利人认可修改版。依许可要求声明：

  > Any modifications made to the original model in this Derivative Work are not endorsed, warranted, or guaranteed by the original right-holder of the original model, and the original right-holder disclaims all liability related to this Derivative Work.

- **BepInEx 6 IL2CPP**：来自 [BepInEx 官方构建 788](https://builds.bepinex.dev/projects/bepinex_be/788/)，采用 LGPL-2.1；协议全文见 `BepInEx-LICENSE.txt`。项目源码见 [BepInEx 仓库](https://github.com/BepInEx/BepInEx)。
- **Unity 2022.3.43 基础库缓存**：`BepInEx/unity-libs/2022.3.43.zip` 是 BepInEx 首次运行通常从 `unity.bepinex.dev` 获取的基础库，用于在用户电脑上生成 IL2CPP 接口；Unity 组件的权利归 Unity Technologies，未按本项目 MIT 许可证授权。
- **UnityDoorstop 游戏适配版**：`winhttp.dll` 基于 [UnityDoorstop 4.5.0](https://github.com/NeighTools/UnityDoorstop/releases/tag/v4.5.0) 修改，处理本游戏的加载入口；采用 LGPL-2.1。协议见 `UnityDoorstop-LICENSE.txt`，对应修改源码和构建脚本随包在 `UnityDoorstop-A1-source/`。该 DLL SHA-256 为 `9e282a33d82356df13ddf50a4b6c1d034ac72b9585b82e861b5d064c79b4773b`。
- **audio.cpp / NAudio / CUDA / Visual C++ 运行库**：audio.cpp 采用 Apache-2.0，许可见 `.cache/audiocpp/runtime/LICENSE`；NAudio 采用 MIT，许可见 `NAudio-LICENSE.txt`；NVIDIA CUDA 库受 [CUDA EULA](https://docs.nvidia.com/cuda/eula/index.html)约束。
- **NPC WAV、默认参考音和游戏角色资料**：发布者已确认公开再分发授权；它们不在本项目 MIT 许可证范围内，也不是可任意再训练、克隆或转售的开放数据集。游戏名称和角色权利归相应权利人所有。

本 MOD 与游戏开发商及上述第三方项目均无官方从属关系。不得将本包解释为游戏本体或官方语音服务。
