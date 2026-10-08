# Mod Manager 支持

当前源码 v0.7.3 实现通用 `IManagedFeaturePlugin` 接口。兼容的 BepInEx Mod Manager 可以读取和切换语音功能；没有管理器时，TTS 插件仍可独立运行。历史 v0.5.9 包不包含该接口。

## 安装

`LocalModManager.Abstractions.dll` 放在 `BepInEx/plugins/` 根目录。TTS 插件和 NAudio 三组件放在 `BepInEx/plugins/A1IndexTTSMod/`。管理器必须使用相同程序集身份的共享接口，不要在多个插件目录放置不同版本。

## 行为

开关持久化至 `Stage3Mvp.Enabled`。开启后连接或启动 TTS 服务；关闭后取消合成、停止播放并停止本插件启动的 audio.cpp 服务。外部独立启动的服务由原管理者控制。程序集不会被热卸载。

开关为期望状态；Starting、Running、Stopping、Failed 为实际生命周期状态。操作失败时请检查 BepInEx 日志。游戏内语音设置面板对启用和关闭分别显示确认提示。

## 接口源码

共享契约见 [IManagedFeaturePlugin.cs](../src/managed-feature-api/IManagedFeaturePlugin.cs)，独立构建项目见 [LocalModManager.Abstractions.csproj](../src/managed-feature-api/LocalModManager.Abstractions.csproj)。管理器应发现实现通用接口的插件，不依赖某个 TTS 的内部类型。