# 安装、升级与卸载

> **IndexTTS 历史文档，已停止维护。** 当前 v0.7.0 起维护 CosyVoice 与增强情感功能，新装请使用 [CosyVoice 分体安装说明](packaging/INSTALL-COSY-SPLIT.md)。下文包名、默认后端和版本保留历史语境，原因见 [路线归档](docs/archive/INDEXTTS.md)。

当前源码版本：A1IndexTTSMod `v0.7.3`，Windows x64，已验证的《不问凡尘》游戏构建 GUID `0acccbcdb9a14aa3a528bd4d850c3202`。工坊包的安装器步骤见 [工坊安装说明](packaging/INSTALL-WORKSHOP.md)。下文保留历史发行包的下载文件名；请先退出游戏。

## 1. 安装 BepInEx

需要 BepInEx 6 Unity IL2CPP Windows x64 加载器。已为其他 MOD 安装并能正常加载的用户可保留现有版本；本项目验证版本是 `6.0.0-be.788+5b766a3`。新装用户从 [BepInEx 官方构建下载](https://builds.bepinex.dev/projects/bepinex_be/788/BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.788%2B5b766a3.zip)，将其压缩包内容放在游戏根目录，即 `WorldApart.exe` 所在位置。该官方 ZIP 的 SHA-256 是 `f4cc496bd098a0df4164b81e3737297707f13a47c2478dba2f60eefab784817a`。

不要把 BepInEx 解压进 `A1IndexTTSMod` 子目录。首次运行游戏后应生成 `BepInEx/LogOutput.log` 和 `BepInEx/interop`；若加载器尚未正常工作，MOD DLL 不会加载。

## 2. 安装 MOD 包

历史发行包可从 [GitHub Releases](https://github.com/havoc123/A1IndexTTSMod/releases) 下载，例如 `A1IndexTTSMod-v0.5.8-win64.zip` 和对应的 `SHA256SUMS.txt`，按校验文件确认 ZIP 散列。v0.7.0 工坊包可双击 `安装TTSMod.bat`，输入游戏路径并确认安装；也可将包内 `A1` 文件夹里的**内容**合并到 `WorldApart.exe` 所在的游戏根目录。当前版本安装后应有：

```text
A1/
  WorldApart.exe
  BepInEx/plugins/A1IndexTTSMod/A1IndexTTSMod.dll
  BepInEx/plugins/A1IndexTTSMod/NAudio.Core.dll
  BepInEx/plugins/A1IndexTTSMod/NAudio.Wasapi.dll
  BepInEx/plugins/A1IndexTTSMod/NAudio.WinMM.dll
  BepInEx/plugins/LocalModManager.Abstractions.dll
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

若没有声音，先看 `BepInEx/LogOutput.log` 中是否有 `A1 IndexTTS Mod 0.7.0`、`Stage3`、`audio.cpp` 和 `playback backend=wasapi_shared`；再看控制台是否显示模型/端口错误。确认 BepInEx、模型路径、驱动和音频输出设备。该 MOD 使用系统默认播放设备，声音不跟随游戏音量滑杆。

## 可选：Mod Manager 控制（源码集成版 v0.7.0）

历史 v0.5.9 插件包不含此功能插件接口。若使用集成版 v0.7.0，请安装实现 `IManagedFeaturePlugin` 接口的兼容 Mod Manager，并确保共享抽象 DLL 与插件一起安装。目录应类似：

```text
BepInEx/plugins/
  LocalModManager.dll
  LocalModManager.Abstractions.dll
  A1IndexTTSMod/
    A1IndexTTSMod.dll
    NAudio.Core.dll
    NAudio.Wasapi.dll
```

官方仓库当前版本的 Mod Manager 尚不一定包含该接口；本项目的实现基于 `havoc123/WorldApart-ModManager-Public` 的 `codex/bepinex-feature-plugin-api` 功能分支。接口及给作者的提交信息见 [Mod Manager 支持说明](docs/MOD-MANAGER-SUPPORT.md) 与 [作者合并说明](docs/MOD-MANAGER-AUTHOR-HANDOFF.md)。

在游戏设置页的 MOD 管理页面中，找到“BepInEx 功能插件”区域即可控制 IndexTTS。开关保存 `Stage3Mvp.Enabled` 配置。关闭会取消等待中的合成、停止当前播放，并停止本插件启动的 audio.cpp；再次开启会恢复服务。BepInEx 不会热卸载插件程序集。若 `127.0.0.1:8892` 已有外部服务，插件只连接它，不会替用户关闭该服务。状态显示 `Failed` 时，开关仍表示配置的期望启用状态，旁边的状态文字说明失败原因。

## 升级与卸载

升级前退出游戏和 TTS 控制台，备份自己修改过的 `config/emotions.json` 与新增 WAV，再以新版包替换此 MOD 管理的文件。不要覆盖其他 MOD 的 BepInEx 文件。卸载时退出游戏后删除 `BepInEx/plugins/A1IndexTTSMod` 和游戏根目录 `A1IndexTTSMod`；如 BepInEx 由其他 MOD 共用，请保留它。`.state` 是本地运行数据，删除 MOD 目录时也会被删除。

## 中文流式语音输入（当前源码分支）

退出游戏并等待 Steam 云同步完成，构建及安装插件后，按需要安装以下模型。两者可以同时安装，运行时只加载所选的一个：

```powershell
.\scripts\Install-ASR.ps1 -GameRoot 'E:\你的游戏目录' -Profile lightweight14m
.\scripts\Install-ASR.ps1 -GameRoot 'E:\你的游戏目录' -Profile accurate160m
```

轻量档为现有 14M FP32 中文流式模型，三份权重约 55.6 MB；准确档为 2025-06-30 的约 160M FP16 中文流式模型，三份权重约 314.1 MB。这些是磁盘大小。源码新装仍默认轻量；在“语音设置 → 语音输入 → 识别模型”选择已安装的准确档。准备、录音与收尾期间禁止切换。启用后在插件启动时按已保存的档位异步加载、预热，再显示就绪；不等待首次点击麦克风。预热期间仍可键盘输入。空闲、对话关闭和切后台均保留模型；只有关闭 ASR、切换模型或退出才释放。麦克风只在主动开始录音后采集。

默认读取 Windows 系统默认麦克风，WASAPI 输入转为单声道 16 kHz。点击发送键左侧麦克风开始，录音时图标持续高亮，再点停止。录音中临时文字可能调整；停止后排空采集样本、完成尾部解码和整段复核，最终草稿可手工编辑并发送。录音时直接点击发送，会先结束录音，等待终稿后按此次点击发送一次；再点麦克风或达到 60 秒上限只停止并保留草稿。取消、切换 NPC 或手动编辑会撤销待发送操作。设置页的测试不写入 NPC 草稿。

两个模型使用 sherpa-onnx 1.13.8 / ONNX Runtime CUDA、4 路候选搜索。兼容的 NVIDIA 驱动和 CUDA 12.x 运行库须已安装；安装器将官方 sherpa CUDA 运行库和 NVIDIA cuDNN 9.14 CUDA 12 DLL 放在独立 `A1IndexTTSMod\asr\runtime`。全部权重逐文件检查字节数与 SHA256，新模型固定仓库 revision；验证完整后替换模型目录。下载失败不覆盖旧模型。依赖缺失会显示错误。安装后离线识别。UI 的“CUDA 请求”表示配置，完整算子放置与游戏帧时间仍需单独测量。

当前源码分支另有原生热词筛选修复：让热词分数参与候选筛选，并在停止录音时撤回未完成词组的加分。安装基础 ASR 后，使用装有 CMake 和 Visual Studio C++ 工具的登录用户终端，在游戏关闭时执行 `scripts/Build-AsrNative.ps1` 和 `scripts/Install-AsrNative.ps1 -GameRoot 'E:\你的游戏目录'`。两档分别适配权重；v2 在说话期间使用 4 路候选，结束后复用同一识别器，以 8 路候选重新解码完整录音。若复核丢失流式终稿中已完整出现的热词，保留流式终稿；不做强制错字替换。未安装 v2 时只完成原有流式收尾。安装器保留旧 DLL 备份。步骤、离线数据与限制见 [原生修复说明](native/asr/README.md) 和 [正确率对照](docs/ASR-CONTEXTUAL-PRUNING-VALIDATION-20261009.md)。

游戏热词来自仓库中的游戏 NPC 名称表，94 个词及来源记录由 `scripts/Generate-AsrHotwords.ps1` 生成；`Install-Plugin.ps1` 同步两个图标和热词配置。修改 `A1IndexTTSMod\config\asr-hotwords.zh-CN.txt` 后，在识别器重新加载时生效。两档目前均可编码并启用全部 94 个游戏词，包括苏倾盏、焚天宗。准确档新增匹配且校验 SHA256 的 `bpe.model`，安装旧版准确档的用户需要重新运行一次上面的准确档安装命令；不需安装 Python 或额外推理模型。热词按完整 token 路径参与流式解码，准确档同时覆盖句首和句中，未做识别后字符串替换。每档独立生成 `.state\asr-hotwords\<档位>\validated.txt`、`skipped.txt`、`encoded.tsv`，分别用于查看启用词、跳过词和实际编码。准确档另生成供原生接口使用的 token 别名与路径文件。生僻字路径使用较强的权重补偿，并按 token 长度分摊；成功加载不保证每次识别正确，具体离线正负对照见验证记录。

准确档权重许可与运行库许可分别记录，参见 [第三方说明](THIRD_PARTY_NOTICES.md)。当前是源代码分支及本地安装，现有 Release 安装包不会随源码自动升级。实测范围见 [ASR 验证记录](docs/ASR-QUALITY-VALIDATION-20261009.md)。

本次常驻资源实测、启动/发送回归和复核限制见 [启动、发送与整段复核验证](docs/ASR-STARTUP-SEND-REVIEW-20261009.md)。
