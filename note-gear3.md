
| 2026-10-19 | W5-terminal-gear-3 `Fika-47965-refactor-20261019-W5-terminal-gear-3.zip` | 军械库改为**静态快照**：注入前用 `ItemBinarySerializer.SerializeItem` + `DeserializeItem(..., Singleton<ItemFactory>.Instance, new Dictionary<MongoID,Item>())` 深拷贝装备树，柜中不再与玩家背包联动；克隆失败则该玩家不注入并告警；三项目 0 error |
