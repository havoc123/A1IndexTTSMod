# Mod Manager 作者合并说明

本项目的 IndexTTS 控制支持建立在管理器 fork 的通用功能插件 API 上。目标是让管理器作者可以将接口和 UI 改动合并回管理器项目；这里记录改动位置和验证边界，方便代码审查。

## 管理器改动

- Fork：[`havoc123/WorldApart-ModManager-Public`](https://github.com/havoc123/WorldApart-ModManager-Public)
- 基线：`main`
- 本地功能分支：`codex/bepinex-feature-plugin-api`
- 提交：`cd57e55` — `Add generic BepInEx feature plugin API`
- 分支现已推送到 `havoc123/WorldApart-ModManager-Public`。
- API 文档：管理器仓库 `docs/BepInEx-Feature-Plugin-API.md`
- 可转交的邮件补丁：[0001-Add-generic-BepInEx-feature-plugin-API.patch](../contrib/0001-Add-generic-BepInEx-feature-plugin-API.patch)

提交增加 `LocalModManager.Abstractions.dll` 和 `IManagedFeaturePlugin`，由管理器从 BepInEx IL2CPP Chainloader 已加载实例发现实现者，并在现有 MOD 设置页增加独立的功能插件区域。原生 `ModRegistry` 包的读取与切换路径保持独立。功能行显示期望状态、运行状态及状态说明。

目前尚未向 `scwunai` 发 PR 或发送消息。可以从 [GitHub 比较页面](https://github.com/scwunai/WorldApart-ModManager-Public/compare/main...havoc123:codex/bepinex-feature-plugin-api?expand=1) 创建 PR，目标仓库选 `scwunai/WorldApart-ModManager-Public`、目标分支选 `main`，来源选 `havoc123/WorldApart-ModManager-Public:codex/bepinex-feature-plugin-api`。也可以从本仓库 `contrib/` 目录取邮件补丁，将文件放到管理器仓库后运行 `git am 0001-Add-generic-BepInEx-feature-plugin-API.patch`。提交前请确认上游 `main` 未新增冲突改动。

## TTSMod 对接

TTSMod 私有开发历史中的提交 `95ec739` 实现接口；同步到公开仓库的对应提交为 `afd8f31`。它使用 `Stage3Mvp.Enabled` 持久化开关状态，并在关闭时取消朗读工作、停止播放及本插件拥有的 audio.cpp 生命周期。TTSMod 保留共享接口的源码副本，使独立构建不要求引用完整管理器项目；两个副本必须保持一致。

集成发行包必须把一个 `LocalModManager.Abstractions.dll` 放在 `BepInEx/plugins/` 根目录，让管理器和 TTSMod 解析到同一契约程序集。管理器不需要引用 TTSMod 的程序集，也不应在状态失败时通过卸载 BepInEx 插件来关闭功能。

## 验证与限制

- 管理器功能分支对 A1 游戏 interop 构建：0 警告、0 错误。
- TTSMod v0.6.0 Release 构建：0 错误；NuGet 漏洞数据源不可达产生 NU1900 警告。
- Steam/BepInEx 共载、通用接口发现、快速启停、待处理合成取消、服务停用和重新开启已验证。完整结果见 TTSMod 的 [集成验证记录](../MOD-MANAGER-INTEGRATION-VALIDATION.md)。
- 尚未验证真实 NPC 回复的可听播放和新版功能状态行的最终截图；当前 public release 也没有发布这套接口。
