
# 方案 A：主机把装备写入军械库容器 loot（2026-10-19 定稿）

## 已核实的代码形状（本仓库）
- 插入点：`HostGameController.InitializeLoot(LocationSettings.Location location)`，`Main/GameMode/HostGameController.cs:1891-1906`。
  现有流程：`NoLoot` 处理 → `FilterLootForHostProfile(location.Loot)` → `ItemBinarySerializer.SerializeLootData(...)` → `PutEFTLootDataDescriptor`。
  **注入必须发生在 `SerializeLootData` 之前**（即 :1900 前）。
- `location.Loot` 类型 `JsonType.LootData`，可 `foreach` 出 `JsonLootItem`；每个条目有 `ValidProfiles`（`string[]`）。
- `FilterLootForHostProfile`（:1916-1948）只丢"`ValidProfiles` 非空且不含主机 profileId"的条目 ⇒ **注入条目把 `ValidProfiles` 留 null 就不会被过滤**。
- 构造 `LootData`：`new JsonType.LootData(Il2CppSystem.Collections.Generic.List<JsonLootItem>)`（:1942 已这样用）。

## 注入条目形状（每个玩家一条）
```csharp
var entries = new Il2CppSystem.Collections.Generic.List<JsonLootItem>();
foreach (var existing in location.Loot) entries.Add(existing);   // 保留原有

for (var i = 0; i < participants.Count; i++)
{
    entries.Add(new JsonLootItem
    {
        Id = armoryContainerId,        // container_Terminal_Design_Stuff_00159/00161/00164/00165/00167 之一
        IsContainer = true,
        ValidProfiles = null,          // 关键：留空，避免被 FilterLootForHostProfile 丢掉
        Item = BuildEquipmentItemTree(participants[i])   // 待实现：玩家装备+口袋+背包（含子物品）
    });
}
location.Loot = new JsonType.LootData(entries);
```

## 待解决（下一轮的实现重点）
1. `JsonLootItem.Item` 的确切类型与构造方式（interop dump `mods/fika-47965/analysis/interop-shape-diff/out/full-new-Assembly-CSharp.txt` 里查 `JsonLootItem` 成员）。
2. `BuildEquipmentItemTree(player)`：
   - 主机自己：`Profile.Inventory.Equipment` 直接可读；
   - 远端玩家：主机只有 `ObservedPlayer` 的 `ItemDescriptor` 副本，需要 `ItemFactory.FlatItemsToStash` / `CreateItem(string, string, UnparsedData)` 重建（代理已给出这两个 API 名）。
3. 5 个容器实例与玩家的对应：`FindObjectsOfType<LootableContainer>(true)` 过滤 `Template == "67a0e494e8fc6968ef0fc9da"`，按 `Id` 序数排序后按参与者序号取模。
4. 若某玩家装备树构建失败 → **该玩家不注入**（保持原版：不丢不搬），并 `LogWarning` 指明 profileId。
5. 安全箱：不在装备树里搬（保留），需实测确认。

## 为什么选 A
- 与其他玩家的可见性一致（走 `location.Loot` 同一通道）；
- 客机不需要额外逻辑（收到 loot descriptor 即拥有容器内容）；
- 5 个容器 = 5 名玩家的数据形状天然吻合。
