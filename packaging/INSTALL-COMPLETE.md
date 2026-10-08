# 不问凡尘 AI NPC 语音 MOD v0.5.8 整合版

> 历史 IndexTTS 安装说明，已归档。当前维护 [CosyVoice 分体安装](INSTALL-COSY-SPLIT.md)，原因见 [路线归档](../docs/archive/INDEXTTS.md)。

本整合版已经包含 BepInEx 6 IL2CPP、适配当前游戏构建的 Doorstop 代理、MOD 插件、audio.cpp Windows CUDA 运行时、IndexTTS 2.5 Q8 GGUF 模型、1169 份 NPC 参考音和默认男女音。需要合法安装的《不问凡尘》Windows 版、NVIDIA 显卡及合适驱动；不包含游戏本体、存档或官方 AI 对话额度。当前已验证的游戏构建 GUID 为 `0acccbcdb9a14aa3a528bd4d850c3202`。

## 安装

1. 将 `A1IndexTTSMod-v0.5.8-complete-r3-win64.7z.001` 和 `.002` 放在同一文件夹，确认两个分卷都已下载完整。
2. 安装 [7-Zip](https://www.7-zip.org/)，**只解压 `.001`**，解压目标选为 `WorldApart.exe` 所在的游戏根目录。压缩包内部没有额外的 `A1` 外层目录；不要单独解压 `.002`。
3. 从 Steam 启动游戏。整合包附带此游戏 Unity 版本所需的 BepInEx 基础库缓存，首次启动无需从 BepInEx 网站下载；仍需在本地生成 IL2CPP 接口，可能等待约 1 分钟。看到 BepInEx 和 audio.cpp 控制台后，载入存档并使用游戏内置 AI 与 NPC 对话。关闭 TTS 功能时会停止本插件启动的 audio.cpp；若 8892 端口已有外部服务，插件只连接并不会关闭它。正常退出游戏时，由插件启动的 audio.cpp 控制台也会关闭。

如果已有其他 BepInEx MOD，安装前先备份游戏根目录的 `winhttp.dll`、`doorstop_config.ini`、`.doorstop_version`、`BepInEx` 和 `dotnet`。本整合版面向干净安装，不保证与不同版本的加载器或更新后的游戏构建兼容。

## 核对与排错

解压后应同时存在 `WorldApart.exe`、`winhttp.dll`、`BepInEx/core/BepInEx.Unity.IL2CPP.dll`、`BepInEx/plugins/A1IndexTTSMod/A1IndexTTSMod.dll`、`BepInEx/plugins/LocalModManager.Abstractions.dll` 和 `A1IndexTTSMod/.cache/audiocpp/models/IndexTTS2.5-GGUF/index-tts2_5-q8_0.gguf`。模型 SHA-256 为 `5e827b2072042e4a1b21ccf24a5cb4f71cb1011403067a0a9b039311d8b38628`。

没有语音时查看 `BepInEx/LogOutput.log` 和 audio.cpp 控制台。若端口 `8892` 被占用，关闭其他 audio.cpp 实例后重启游戏。声音走系统默认播放设备，不随游戏音量滑杆变化。诊断采集默认关闭；运行后生成的 `.state` 是本地状态，不是发行包里的用户记录。请勿公开分享存档、完整对话提示词或令牌。

先用随分卷提供的 `SHA256SUMS-complete-r3.txt` 校验两个文件，再解压。包内第三方许可和适配版 Doorstop 源码位于 `A1IndexTTSMod/third_party/`。本 MOD 是非官方社区作品，源码见 https://github.com/havoc123/A1IndexTTSMod 。
