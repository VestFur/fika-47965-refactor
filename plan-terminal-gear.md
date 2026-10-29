
# 终点站装备没收 —— 实现方案（数据已确证，2026-10-19）

## 一、数据（SPT 1.2.0 数据层确证）
| 对象 | id | 说明 |
|---|---|---|
| 军械库玩家容器 | `67a0e494e8fc6968ef0fc9da` = `lc_spec_stash_player_terminal` | 10×20，Filter=Item，ExcludedFilter=MobContainer；terminal 图刷 **5 个**（= MaxPlayers 5，一人一个），全在 `Terminal_Area_05_Administration`，`IsAlwaysSpawn=true`；**staticLoot 恒空**，只能外部注入 |
| 军械库钥匙 | `68a4b745b63e97cc5a056a07` = `key_terminal_arsenal` | 尸体容器 `69122598e5b8725fb10f6a93`（`lc_corpse_soldier_terminal_key`，Terminal_Area_03_Substation_Courtyard）staticLoot **100%** 刷；另有一处 loose 必刷点 |
| 随机物资容器 | `68a5b9af76a25d8e6e0e2e56` = `lc_spec_stash_random_terminal` | 仅作对照（有 349 条战利品） |

## 二、执行层结论
- 客户端/元数据对上述 id **0 命中** ⇒ 原版**不是客户端硬编码**；线上由 BSG 后端在生成 `location.Loot` 时写入，SPT 侧**未实现**。
- `terminal/base.json` 里 `saveItems: false`（:2618）⇒ SPT 战后**不保存也不删除**玩家背包；纯客户端搬运不会改变档案，**"丢装备"在 SP 下本来就不存在**。
- 因此 Fika 要做的是**局内**重现这套玩法：装备进容器 → 玩家取回；不需要改 SPT 服务端。

## 三、Fika 实现分工
- **各机对自己的玩家执行**（`HostInventoryController` 只能操作本机 player；`ObservedPlayer.SetInventory` 只是重建装备副本）。
- 容器内容由**主机的 loot descriptor** 统一下发（`HostGameController.InitializeLoot:1891-1906` → `ClientGameController.InitializeLoot:458-463`）。
- 坑：`HostGameController.FilterLootForHostProfile:1908-1943` 会丢弃 `ValidProfiles` 不含主机的条目 —— 若走"服务端按档注入"，必须先改这个过滤器。

## 四、最小实现（W5-terminal-gear）
1. **主机**：`CoopGame.InitPlayer` 的 `SpawnLoot` 之后（`CoopGame.cs:503-504`）扫世界里的 5 个 `LootableContainer`（TemplateId=`67a0e494e8fc6968ef0fc9da`），按 NetId 升序把第 i 个分配给第 i 名玩家，向全体广播 `TerminalArmoryPacket{ NetId → containerId }`。
2. **每台机器**：收到分配（或主机本机直接执行）后，对本机玩家执行：
   - 取 `Equipment` 全部槽位 + `Inventory.Equipment`/背包内容；
   - 用 `ItemManipulator.Move` / `Grid.AddItemWithoutRestrictions`+`FindFreeSpace`（参考 `FikaServer.Callbacks.cs:80`、`FikaCommands.cs:429-431`）把每件物品移入分配到的容器的 `Grid`；
   - 安全箱（SecureContainer）**不搬**（原版保留，需实测确认）。
3. **失败兜底**：容器没找到/网格满 → 把物品放回玩家背包并 LogWarning（不静默吞）。
4. **客机与主机一起触发**：分配包由主机在 loot 初始化完成后发一次；客机在 `FikaClient.RegisterPacket` 里注册处理。

## 五、待确认
- 安全箱是否保留（原版实测）；
- 尸体钥匙容器是否需要在联机下强制刷（`IsAlwaysSpawn` 已在数据里，理论上会刷）；
- 客机执行搬运后，容器内容是否需要同步给主机（只在取回时由容器 owner 同步，通常不需要）。
