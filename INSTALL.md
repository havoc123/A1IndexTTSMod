# 安装、升级与卸载

适用版本：A1IndexTTSMod `v0.5.8` 预发布版，Windows x64，当前已验证的《不问凡尘》游戏构建 GUID `0acccbcdb9a14aa3a528bd4d850c3202`。请先退出游戏。

## 1. 安装 BepInEx

需要 BepInEx 6 Unity IL2CPP Windows x64 加载器。已为其他 MOD 安装并能正常加载的用户可保留现有版本；本项目验证版本是 `6.0.0-be.788+5b766a3`。新装用户从 [BepInEx 官方构建下载](https://builds.bepinex.dev/projects/bepinex_be/788/BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.788%2B5b766a3.zip)，将其压缩包内容放在游戏根目录，即 `WorldApart.exe` 所在位置。该官方 ZIP 的 SHA-256 是 `f4cc496bd098a0df4164b81e3737297707f13a47c2478dba2f60eefab784817a`。

不要把 BepInEx 解压进 `A1IndexTTSMod` 子目录。随后安装第 2 步的 r3 主包；它已包含修复后的 `winhttp.dll`、BepInEx 首次运行所需的 Unity 基础库缓存和配置。请先安装官方 BepInEx，再用 r3 主包覆盖同名文件。旧版 r1/r2 或旧 GitHub 主包用户可改用同一 Release 的 `A1IndexTTSMod-v0.5.8-portable-doorstop-patch.zip` 覆盖游戏根目录，无需运行检查脚本。修复后的 DLL SHA-256 为 `9e282a33d82356df13ddf50a4b6c1d034ac72b9585b82e861b5d064c79b4773b`。

首次运行游戏后应生成 `BepInEx/LogOutput.log` 和 `BepInEx/interop`；若加载器尚未正常工作，MOD DLL 不会加载。

## 2. 安装 MOD 包

从 [GitHub Releases](https://github.com/havoc123/A1IndexTTSMod/releases) 下载 `A1IndexTTSMod-v0.5.8-r3-win64.zip` 和 `SHA256SUMS-r3.txt`，按校验文件确认 ZIP 散列。解压 ZIP，将其中 `A1` 文件夹里的**内容**合并到 `WorldApart.exe` 所在的游戏根目录。最终应有：

```text
A1/
  WorldApart.exe
  winhttp.dll
  BepInEx/config/BepInEx.cfg
  BepInEx/unity-libs/2022.3.43.zip
  BepInEx/plugins/A1IndexTTSMod/A1IndexTTSMod.dll
  BepInEx/plugins/A1IndexTTSMod/NAudio.Core.dll
  BepInEx/plugins/A1IndexTTSMod/NAudio.Wasapi.dll
  A1IndexTTSMod/scripts/Run-AudioCppForGame.ps1
  A1IndexTTSMod/.cache/audiocpp/runtime/audiocpp_server.exe
  A1IndexTTSMod/references/demo.wav
  A1IndexTTSMod/references/default_male.wav
  A1IndexTTSMod/references/default_female.wav
  A1IndexTTSMod/references/npcs/100000.wav  # 其余 NPC 音频也在此目录
  A1IndexTTSMod/references/npcs/npc_id_name.csv
```

发行 ZIP 中已经包含 NPC 音频，无需逐个复制仓库文件。不要把 ZIP 中的外层 `A1` 目录再套进现有 `A1` 目录。

## 3. 下载模型

从 [audio.cpp 的 IndexTTS2.5 GGUF 页面](https://huggingface.co/audio-cpp/audio.cpp-gguf/tree/main/IndexTTS2.5-GGUF)下载 `index-tts2_5-q8_0.gguf`，放在：

```text
A1/A1IndexTTSMod/.cache/audiocpp/models/IndexTTS2.5-GGUF/index-tts2_5-q8_0.gguf
```

本次验证的 Q8 文件大小为 `3502955328` 字节，SHA-256 为 `5e827b2072042e4a1b21ccf24a5cb4f71cb1011403067a0a9b039311d8b38628`。若上游模型更新，散列可能变化，先核对模型来源及说明。模型许可独立于本 MOD 的 MIT 许可证。

## 4. 首次运行

从 Steam 启动游戏、载入存档并使用游戏内置 AI 与 NPC 对话。MOD 会打开可见的 audio.cpp 控制台，载入和预热模型后朗读 NPC 回复；首次启动可能较慢。游戏正常退出时该控制台应自动结束。不要同时手工启动占用 `127.0.0.1:8892` 的另一个 audio.cpp 实例。

若没有声音，先看 `BepInEx/LogOutput.log` 中是否有 `A1 IndexTTS Mod 0.5.8`、`Stage3`、`audio.cpp` 和 `playback backend=wasapi_shared`；再看控制台是否显示模型/端口错误。确认 BepInEx、模型路径、驱动和音频输出设备。该 MOD 使用系统默认播放设备，声音不跟随游戏音量滑杆。

## 升级与卸载

升级前退出游戏和 TTS 控制台，备份自己修改过的 `config/emotions.json` 与新增 WAV，再以新版包替换此 MOD 管理的文件。不要覆盖其他 MOD 的 BepInEx 文件。卸载时退出游戏后删除 `BepInEx/plugins/A1IndexTTSMod` 和游戏根目录 `A1IndexTTSMod`；如 BepInEx 由其他 MOD 共用，请保留它。`.state` 是本地运行数据，删除 MOD 目录时也会被删除。
