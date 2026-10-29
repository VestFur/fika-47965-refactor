
| 2026-10-19 | 方案选定 | 终端装备没收采用 **方案 A**（主机在 `InitializeLoot` 序列化前把每个玩家的装备树注入军械库容器条目，`ValidProfiles=null` 以绕过 `FilterLootForHostProfile`）；细节见 `plan-terminal-gear-A.md`。已核实 `location.Loot = JsonType.LootData` 可 foreach、注入点必须在 `HostGameController.cs:1900` 之前 |
