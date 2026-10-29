# Fika 配置项审计报告（2026-10-20）

审计对象：`mods/fika-47965-refactor/source/Client/Fika.Core/FikaConfig.cs` 及其消费者。
方法：解析每个 `NAME = SetupSetting(..., "Label", ...)`，统计 **FikaConfig.cs 之外** 对该属性的引用数（`consumers`）与 `.Value` 读取数（`values`）。0 表示除配置定义外无人读取（或仅被配置内部接线到某个静态字段）。

结论分三类：**A 删除**（死代码/功能已不存在）、**B 合并或隐藏**（只是调参旋钮，保留一个总开关）、**C 必须验证**（有行为风险或疑点）。

---

## A. 建议删除

| 选项 | 键名 | 证据 | 说明 |
|---|---|---|---|
| Rally: Skip Vote Timeout | `RallySkipVoteTimeoutSeconds` | consumers=0，`FikaRallyGate` 已在 W3-rally 整块删除 | 功能已不存在，配置残留 |
| Transit: Countdown Fallback | `TransitCountdownFallbackSeconds` | consumers=0 | 兜底已随 `TransitRecovery` 删除 |
| Transit: Suppress Co-Located Exfiltration | `TransitSuppressCoLocatedExits` | consumers=0 | 无消费者 |
| Cutscene: Native Tick Hook | `CutsceneNativeTickHook` | consumers=0（配置内只赋值给静态字段，全树无人读取） | 早期"客机 CG"实验残留；客机 CG 现由 `FikaLocalCutsceneTrigger` 负责 |
| Write Online Player Counter | `WriteOnlinePlayerCounter` | consumers=1（仅 `MainMenuUIScript`），且该值从未 `.Value` 读取 | 写了不读，属于 UI 实验残留 |

## B. 合并 / 隐藏（保留总开关，删掉细粒度旋钮）

| 选项 | 键名 | 现有消费者 | 建议 |
|---|---|---|---|
| Online Players Fast Refresh / Refresh Seconds / Idle Refresh Seconds | `OnlinePlayersFastRefresh` / `OnlinePlayersRefreshSeconds` / `OnlinePlayersIdleRefreshSeconds` | 各 1（`MainMenuUIScript`） | 合并为一个"刷新间隔"或直接写死默认值，只留 `Enable Online Players` |
| Load Sync: Host Peer Timeout / Client Host-Wait Ceiling / Client Transit Reload Ceiling / Silent Warning Seconds | `LoadSyncHostPeerTimeout` / `LoadSyncClientHostWaitCeiling` / `LoadSyncClientTransitCeiling` / `LoadSyncSilentWarnSeconds` | 0（均只接线到 `LoadSyncGate` 的静态字段，字段本身被使用） | 不是死代码，但属于"调参旋钮"：保留 1 个总开关 + 内部常量，四项从界面隐藏 |
| Transit: Client Reload Hold | `TransitReloadHoldSeconds` | 0（接线到内部字段） | 同上，隐藏 |
| Plant Interaction Fix / Plant Consumes Item / Plant Consume On Client | `PlantInteractionFix` / `PlantConsumesItem` / `PlantConsumeOnClient` | 各 1（`PlantInteractionDiagPatch`） | 三个开关控制同一条链，合并为一个 `Plant Relay` 总开关 |
| Transit: Abort When Someone Leaves | `TransitAbortWhenSomeoneLeaves` | 1（`FikaHostTransitController`） | 随"暂停/恢复看门狗"删除后语义已不完整，建议随之一并删除或隐藏 |

## C. 必须验证（有行为风险/疑点）

| 选项 | 键名 | 消费者 | 疑点与验证方法 |
|---|---|---|---|
| Strict Inventory Sync | `StrictInventorySync` | 10（`CoopGame`、`FikaClient`、`FikaClient.Callbacks`、`FikaServer`、`FikaServer.Callbacks`、`IFikaNetworkManager`） | 影响面最大：它改的是库存操作的接受/拒绝路径。建议先在默认配置下关掉做一局对照（拾取/移动/换弹/丢包恢复），若关掉后无异常则整块删除该开关与分支 |
| Pose Write Guards | `PoseWriteGuards` | 7（`ObservedPlayer`、`ObservedMovementContext`、`PoseWriteGuards`） | **不能删**：这是每帧原生 setter 写入的守卫（性能+正确性）。建议改为恒开、从界面隐藏 |
| Ground Loot Pickup Bypass / Auto Fallback | `GroundLootPickupBypass` / `GroundLootPickupAutoFallback` | 各 1（`GroundLootPickupBypassPatch`） | 前者你判断为"遗留未生效"，验证法：关掉它拾取地面物资是否仍正常；若正常则删除前者、只留 Auto Fallback |
| Honor Map Fixed Time | `HonorMapFixedTime` | 1（`CoopGame`） | 验证：在固定时间地图（如 Terminal/Icebreaker）开局时间是否与主机一致；不一致则说明该选项未生效 |
| Countdown: Restore In-Raid HUD | `CountdownRestoreHud` | 2（`BaseGameController`） | 你报告的"UI 固定、改键不生效"高度指向这里：Fika 接管了 HUD 恢复。建议改为**只调用原生的 HUD 恢复入口**（不自己重建 UI），或直接关闭该选项对照一次 |
| Countdown: Play Opening Countdown | `CountdownPlayOpening` | 1（`CountdownRestore`） | 同上，属于 Fika 自实现的倒计时播放；与上面同一处冲突源，二者应一起验证 |
| Sentry: Sync Card Access | `SentrySyncCardAccess` | 0（配置内接线到 `FikaSentryAccess.RepairEnabled`） | 机制存在（`SentryTerminalAccessPatches` + `FikaSentryAccess`），但你实测机枪仍开火：验证主机刷卡后客机 `FikaSentryAccess.Apply` 是否被调用（日志 `result=repair-off` 表示开关为关） |
| Spawn: Mirror Offline Flags | `MirrorOfflineSpawnFlags` | 2（`HostGameController`） | 验证：关掉后客机出生状态（离线标志位）是否与主机一致；不一致说明它确实必要，应保留 |
| Spawn: Ambient Bot Cap Fallback | `EnableAmbientBotCapFallback` | 3（`HostGameController`） | 验证：关掉后多人大厅杂兵数量是否超出上限；若正常则删除 |
| Transit: Require All Teammates Ready | `TransitRequireAllTeammates` | 0 直接引用（配置内接线到 `TransitStartGate.RequireAllTeammatesInteracted`，该字段在 `FikaHostTransitController.EveryoneInteracted` 使用） | **不是无用**：它控制严格门。当前争议在"客机按下是否被记录"，与开关本身无关；先修记录，再决定是否保留 |

---

## 执行顺序建议

1. **A 组 5 项直接删**（无消费者，删了不影响任何路径）；
2. **B 组隐藏/合并**（改界面呈现，不动逻辑），其中 `TransitAbortWhenSomeoneLeaves` 建议直接删；
3. **C 组按风险排序验证**：Strict Inventory Sync → Ground Loot Bypass → CountdownRestoreHud/PlayOpening（UI 冲突）→ Sentry 刷卡 → Spawn 两项 → HonorMapFixedTime；
4. `PoseWriteGuards` 恒开隐藏，不提供关闭入口。
