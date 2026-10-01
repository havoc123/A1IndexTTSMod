# NPC 参考音

专属参考音以游戏中的数字 NPC ID 命名：`<npcId>.wav`，例如 `100000.wav`。MOD 从 `NpcModel.NpcCfgId.Value` 取得 ID，并读取本目录 `npc_id_name.csv`，其列为 `npcId,npcName,audio,gender`（UTF-8）。表中有 1591 个 NPC，其中 1169 个附有参考音。

| `audio` | 含义与运行规则 |
| --- | --- |
| `0` | 无专属 WAV；`gender=1` 用 `references/default_male.wav`，`gender=2` 用 `references/default_female.wav`，`gender=3` 不朗读。 |
| `1` | 有原音，使用 `<npcId>.wav`。 |
| `2` | 有生成参考音，使用 `<npcId>.wav`。 |

如果标为 `1/2` 的专属 WAV 丢失，MOD 使用 `references/demo.wav`。游戏运行期间替换 WAV 后，下一句回复即可生效。参考音应是单人、清晰、无背景音乐且可正常解码的 WAV。音频已纳入 Git 和 Release 包；游戏素材与录音不受项目源码 MIT 许可证覆盖，见根目录 `THIRD_PARTY_NOTICES.md`。

`gender` 取游戏配置原始值：`1` 男，`2` 女，`3` 未知。当前分布为 844、696、51；未知多为灵宠或未设定性别角色。`npc_id_name.csv` 由游戏人物配置建立，适配游戏更新时应重新核对 ID 与性别。仓库中未发布的 `npc_id_gender.csv` 是本地处理中间表，不是运行依赖。
