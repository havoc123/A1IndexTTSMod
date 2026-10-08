# A1 IndexTTS NPC 语音 MOD v0.7.3 安装说明

本包用于《不问凡尘》Windows x64 版。默认使用 NVIDIA CUDA 路由；也可切换为社区 Vulkan 服务端（适用于支持 Vulkan 的 AMD 显卡）。需要可从 Steam 正常启动游戏的 BepInEx 6 Unity IL2CPP 环境，以及另行下载的 IndexTTS2.5 Q8 模型。建议 12 GB 或以上独立显存。工坊订阅只下载文件，不会自动安装。

## 使用安装脚本

1. 退出游戏。先按 [sc_wunai 的环境说明](https://steamcommunity.com/sharedfiles/filedetails/?id=3814675406)安装 BepInEx，或沿用现有可正常加载的 BepInEx 6 IL2CPP 环境。
2. 订阅本条目，打开 `steamapps\workshop\content\4209920\3815220637`。
3. 双击 `安装TTSMod.bat`，输入 `WorldApart.exe` 的完整路径或其所在文件夹；也可以把 `WorldApart.exe` 拖到该 `.bat` 上。核对目标目录后输入 `Y` 安装。
4. 从 [audio.cpp 模型页面](https://huggingface.co/audio-cpp/audio.cpp-gguf/tree/main/IndexTTS2.5-GGUF)下载 `index-tts2_5-q8_0.gguf`，放到游戏根目录下的 `A1IndexTTSMod\.cache\audiocpp\models\IndexTTS2.5-GGUF\index-tts2_5-q8_0.gguf`。模型不包含在工坊包中。
5. 从 Steam 启动游戏，载入存档后与 NPC 对话。首次载入和预热模型需要等待。

安装器只复制本 MOD 文件，不安装或修改 BepInEx 引导。若已有 `LocalModManager.Abstractions.dll` 与本包不同，安装器会停止，避免覆盖管理器共用依赖。已有的 `config/emotions.json`、参考音和模型会保留。没有安装 MOD 管理器也可独立运行本 MOD，但本包附带的共享抽象 DLL 仍是插件运行依赖。

## 手动安装

退出游戏后，将本包 `A1` 文件夹**里面的内容**合并到 `WorldApart.exe` 所在目录，不要再套一层 `A1`。然后按上面的第 4 步放置模型。目标结构应包含：

```text
WorldApart.exe
BepInEx/plugins/A1IndexTTSMod/A1IndexTTSMod.dll
BepInEx/plugins/LocalModManager.Abstractions.dll
A1IndexTTSMod/.cache/audiocpp/runtime/audiocpp_server.exe
A1IndexTTSMod/.cache/audiocpp/models/IndexTTS2.5-GGUF/index-tts2_5-q8_0.gguf
```

若没有声音，查看游戏目录下的 `BepInEx/LogOutput.log`。音频使用 Windows 系统默认输出设备，不跟随游戏音量滑杆。

## 显卡路由

默认配置 `[Stage3Mvp] GpuBackend = Nvidia` 保留原 CUDA 服务端和运行时 DLL。要使用 AMD/Vulkan，退出游戏后编辑 `BepInEx/config/org.a1indextts.mod.cfg`，在 `[Stage3Mvp]` 下设置 `GpuBackend = Vulkan`，再启动游戏。Vulkan 路由使用单独的 `audiocpp_server-vulkan.exe`，配置 `backend = vulkan`，默认 `device = 0`，不会替换或覆盖 `audiocpp_server.exe` 与现有 DLL。多显卡机器请将 `GpuDevice` 设成 AMD 在 Vulkan 设备列表中的序号。服务窗口出现 `Vulkan0` 才表示模型已由 Vulkan GPU 推理。

## 卸载

退出游戏后删除 `BepInEx/plugins/A1IndexTTSMod` 和 `A1IndexTTSMod`。只有确定没有其他插件使用时，才删除 `BepInEx/plugins/LocalModManager.Abstractions.dll`；保留共用的 BepInEx 与管理器环境。
