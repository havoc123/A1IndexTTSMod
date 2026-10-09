# Mod Manager 支持

TTSMod 自 v0.6.0 源码集成版起可由兼容的 Mod Manager 控制，当前源码与工坊打包版本为 v0.7.0。历史 v0.5.9 包是独立插件，不含此接口；发布版支持取决于对应的 TTSMod 和管理器包是否包含接口实现。

## 安装要求

- 游戏使用现有的 BepInEx 6 IL2CPP 安装和 Steam 启动方式。
- 管理器构建须包含通用 `IManagedFeaturePlugin` API。
- `LocalModManager.Abstractions.dll` 与 `LocalModManager.dll`、`A1IndexTTSMod` 一起放在 `BepInEx/plugins/` 的共享插件目录中。不要复制第二份同名抽象 DLL 到 TTSMod 子目录。
- 使用此 TTSMod 源码构建或集成发行包；该构建把共享抽象 DLL 放在插件目录根部。`scripts/Install-Plugin.ps1` 也会安装该文件，并在检测到同名文件时核对散列，避免静默替换不同版本的 API。

典型结构：

```text
BepInEx/plugins/
  LocalModManager.dll
  LocalModManager.Abstractions.dll
  A1IndexTTSMod/
    A1IndexTTSMod.dll
    NAudio.Core.dll
    NAudio.Wasapi.dll
```

管理器和 TTSMod 必须引用相同程序集身份的 `LocalModManager.Abstractions` v1.0。缺少抽象 DLL 或程序集版本不一致时，BepInEx 无法解析插件接口依赖；请一起升级管理器、共享 DLL 和 TTS 插件。

## 开关与状态

管理器设置页的“BepInEx 功能插件”区域会列出实现接口的插件。IndexTTS 的开关持久化到 BepInEx 配置 `Stage3Mvp.Enabled`：

- 开启后插件连接现有 TTS 服务，或启动并等待本插件拥有的 audio.cpp 服务就绪。
- 关闭后拒绝新的朗读请求、取消等待中的 HTTP 合成、停止 WASAPI/WinMM 播放，并只停止本插件自己的 audio.cpp 服务。
- 再次开启会恢复朗读。BepInEx 插件程序集不会被热卸载。
- 如果 `127.0.0.1:8892` 已有服务，IndexTTS 将其视为外部管理的服务；它只连接，不负责停止它。
- 开关显示配置中的期望状态，`Starting`、`Running`、`Stopping` 或 `Failed` 是实际运行状态。失败时开关可能仍为开启，需查看旁边的错误说明和 `BepInEx/LogOutput.log`。

没有安装 Mod Manager 时，插件仍读取自己的 `Stage3Mvp.Enabled` 配置并独立运行；但 v0.7.0 接口构建所需的 `LocalModManager.Abstractions.dll` 仍必须随 TTSMod 安装。

## 给管理器作者

本功能使用通用接口，不依赖 TTS 类型，也不将 IndexTTS 伪装成原生 `mod.json` 包。管理器接口、发现过程、设置页实现、可合并提交和可直接应用的邮件补丁见 [作者合并说明](MOD-MANAGER-AUTHOR-HANDOFF.md)。

本地验证范围包含 Steam 共载、接口发现、快速开关、合成取消、停用后服务退出和重新启用；尚未验证真实 NPC 对话的可听播放，也未在新截图中检查最终状态行排版。交接记录见 [作者合并说明](MOD-MANAGER-AUTHOR-HANDOFF.md)。
