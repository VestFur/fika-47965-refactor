
| 2026-10-19 | 打包口径变更 | Headless 改用**用户已安装的最新版**（`D:\EFT5.0-47965\BepInEx\plugins\fika\Fika.Headless.dll`，232KB / 10-18 16:48），不再用 refactor 树自编译的 Headless；此后所有包沿用该口径 |
| 2026-10-19 | 军械库柜子"镜像玩家背包"诊断 | 注入的 `JsonLootItem.Item` 是**活引用**（玩家 `Inventory.Equipment` 本体），所以柜子与玩家背包实时同步。修法二选一：①注入前深拷贝成独立 `Item` 树（需 `ItemFactory` 类 API，尚未核实）；②注入后把玩家侧原件移除（`ItemManipulator.Discard/Remove`，语义待核实）——移除后柜中那把成为唯一持有者，即变成"入局时的静态快照" |
