# Fika 目标清单（TARGETS）

> 规则（用户 2026-10-27 定）：
> 1. **只有实机验证通过才在对应项标「已解决」**；没解决就把该项对应的补丁**删掉**，再换其他办法，不留冗余无用补丁。
> 2. 不重复踩以前踩过的坑（见每项的"已排除"栏）。
> 3. 新增 bug 往后追加；由某个 bug 衍生的，追加到该项下面（子项 5.1、5.2 …）。
> 4. 每项的"关联补丁"列就是"没解决时要删的东西"，改完/删完都要同步更新这里与 `REFACTOR-TRACKER.md`。

状态标记：`未解决` / `待实机` / `已解决` / `已作废`
当前线上：client `90E792BF…`（W36 版）、server `571F3C78…`、包 `W36-rallygate.zip`；**W37 仅编译未部署**。

---

## T1　终点站转移（海岸线 → 终点站/码头）：移植 CG 进程补丁后客机无法随主机转移

- **现象**：加客机 CG 进程补丁（`FikaLocalCutsceneTrigger`，`UseNativeClientTrigger=true`）→ CG 能正常播，但客机**无法和主机一起转移**；去掉该补丁 → 能转移，但 CG 不触发。
- **重要**：这是**独立问题**，与 T2（先互动后无法撤离）**不是同一件事**，不要合并处理。
- **已排除**：①延时不是原因（`LocalTriggerDelaySeconds` 0→2f 无效，W30 作废，客机 CG 进程**不需要延时**）；②不是 `waitForAllParty` 门（25 号点确实 false，但那只解释"先按"被拒，不解释"CG 进程 ↔ 转移互斥"）。
- **关联补丁**：`FikaLocalCutsceneTrigger`（`UseNativeClientTrigger` / `LocalTriggerDelaySeconds` / `GrantNativeStart`、新增 `PlaybackLeftThisProcess`/`EndOpenLocalProcesses`）、`CutsceneStartSyncPatch`、`ClientTransitController.HandleClientExtract`、`FikaHostTransitController.IssueTransferForClients`
- **本轮实现（W42，已编译未部署）**：①本地 CG 进程原来只靠 `TimelineShowed` 或 **60 秒超时**结束，而转移窗口只有 **20 秒**（`transits[25].time=20`）⇒ 新增第三结束条件 `PlaybackLeftThisProcess`（用 `world.CutscenesClientController._cutsceneProcessId` + `SeenPlaying` 防"刚建好就被关"）；②本机转移落地那一刻（`HandleClientExtract` 里 `game.Extract` 之前）调 `EndOpenLocalProcesses("transit landing of point N")` 收掉本机进程；③**真正的拦截门**：主机 `Native.transitPlayers` 值形状不一致（写 raid id、读 point id）已修，所以"客机因 CG 没按过"不再是死局——只要它站在点内且已被注册就会被带走。
- **判据**：客机 `[CgDiag] local process closed before the transit landing point=25 processes=1`；`[SyncDiag] local-process end` 里**不应**出现 `reason=timeout`；主机侧 `[TransitDiag] transfer-skip` **不应**出现。
- **未证部分**：若"CG 能播但仍不转移"，贴主机 `[TransitDiag] host answer=… reason=point-disabled|entry-requirements-not-met` + 客机 `client answer type=InactivePoint point=25`，那就是"两端 end actions 各跑一次把 `point.Enabled` 翻回去"成立，补法是收进程时只跑非 transit 动作或快照/还原 `point.Enabled`。
- **状态**：待实机

## T2　客机先于主机互动 → 载具撤离点 / 转移点 之后无法撤离

- **现象**：客机先按交互（A: 载具撤离点＝黑车/BTR 付费撤离；B: 转移点 transit），随后无法撤离 / 无法转移。
- **要求**：A 与 B **分别验证、分别修**，不要当成一件事。
- **T2-A 载具撤离点（黑车）**
  - 关联补丁：`ExfilCountdown.Execute`（W42：本机已有激活就永不替换）、`FikaExfilManager.ExfiltrationPoint_OnStatusChanged`（**未修，第二半**）
  - 已排除：W32 的"更早者胜"被证明是错的——它拿**两台不同时钟**的裸时间戳比大小，客机时钟偏小就会把主机倒计时往前推。
  - **本轮实现（W42）**：`ExfilCountdown` 改为"本机已有激活（`>0`）就绝不替换"，日志 `[CarExtract] kept the local activation`。
  - **第二半（W43 已做）**：`FikaExfilManager` 不再无条件销毁——只有当"本机倒计时已无效/走完、玩家不在点位或已死、点位真被关闭（NotPresent/Hidden）"才清；否则保留本机倒计时并打 `[CarExtract] kept the local countdown … remaining Ns`，由本机自己完成结算。
  - 判据：客机 `[CarExtract] kept the local activation`；结局看客机是否拿到 `[ExtractDiag]` 的本机撤离，而不是停在 "exits stay disabled"。
  - 状态：待实机
- **T2-B 转移点（transit）**
  - 关联补丁：`FikaHostTransitController`（`IssueTransferForClients` / `RegisteredParticipants` / `OnHostPlayerExit` / `DropDeadParticipant` / `CountRegisteredParticipants`）、`EveryoneInteracted`（W37）、`FikaHeadlessTransitController`（W42 补修 4 处）
  - 已排除：延时（W30）
  - **本轮实现（W42）真因**：`Native.transitPlayers` **值形状不一致**——`RegisterTransitParticipant` 写 `[profileId] = RaidId`，而五个读者拿它跟 **pointId** 比 ⇒ 已注册的客机被判"没注册"→ `IssueTransferForClients` 跳过**且不解 ban**（`BannedPlayers`+`DisableExits` 留着）；取消倒计时时 `RegisteredParticipants` 恒空 → 谁都不放。修法：五处全部"双形状兼容"，Headless 侧同样的 4 处一并修（用 `FindHumanPlayer` 解析 raidId）。
  - 判据：客机先按 → 主机 `[TransitDiag] start-gate-skip … reason=waitForAllParty-false`，主机按下后 `host countdown start point=25`，转移后 `[TransitDiag] transfer-skip` **不出现**；客机 `client answer type=Confirm point=25` → `client landing transit=25`（真被带走的唯一证据）。
  - 状态：待实机
- **与 T1 的关系**：无关，T1 是"CG 进程 ↔ 转移"互斥，T2 是"谁先按"的时序问题。

## T3　物品拾取卡手（客户端卡在拾取状态）

- **现象**：拾取物品后客户端卡住（UI/交互锁死）。
- **用户判断**：很可能是**服务端没收到拾取请求、或没有返回"拾取成功"**，客户端一直等。
- **关联补丁**：`ClientInventoryOperationHandler`（看门狗 `/FailLocally`、W26 记账）、`NativeInteractionRelease`
- **已排除**：W26 只补了"记账"，不解决"卡手"本身。
- **本轮实现（W41，已编译未部署）根因**：拾取的原生回调没有应答者——`ETFPickUpState` 与 animator 交互队列**只由** `NativeInteractionRelease.Release` 释放，而 `Release` 判"卡"的三个信号里，②`MovementContext.CanInteract` 被占、③animator `IsScheduleDenied` **没有任何看门狗在喂**（`CoopGame.RepairLocalInteractionState` 只按状态名给预算，仅 Pickup 5s / Plant 20s；操作级看门狗要 30s/+20s 才 `FailLocally`）⇒ "服务端不回包"的表现是最长 **50 秒锁死**，若本地已成功只剩 animator 被拒则**整局无人放**。修法：`NativeInteractionRelease.WatchLostInteractionSchedule`（Idle + 门被占或 animator 被拒持续 8 秒 → `Release("lost-interaction-schedule")`），在 `CoopGame.RepairLocalInteractionState`（1 Hz 节流后）调用，每帧零成本。
- **判据**：`[InvDiag] lost-interaction-schedule: idle for 8.x s … releasing the presentation state` + `NativeInteractionRelease[lost-interaction-schedule]: releasing …`
- **状态**：待实机

## T4　客机在自身物品栏移动物品 → 物品变"未搜索"

- **现象**：**普通移动物品没问题**；但是**已装备物品卸下弹夹 / 卸下已装备物品**，会让该物品变成"未搜索"状态。
- **T4.1 卸下弹夹**
  - 关联补丁：`ApplyRelayedOperationBookkeeping`（W11/W26）、`ClientInventoryController.MarkOperationItems*`
  - 状态：未解决
- **T4.2 卸下已装备物品（装备槽 → 背包/弹挂）**
  - 关联补丁：同上
  - 状态：未解决
- **已排除**：不是"所有移动都会触发"（普通移动正常），所以范围应锁定在"装备槽相关操作"的重建/替换路径上。
- **本轮实现（W41，已编译未部署）**：
  - **T4.1 真因（可证）**：`MarkOperationItemsKnown` 只读**外层** operation 的 `IOneItemOperation.Item1`/`ITwoItemOperation.Item2`/`SplitOperation._splitItem`，**不递归 `IParentOperation.InternalOperation`**；而 `MarkOperationItemsSearched` 与 `CaptureResyncItems` **都递归**——只有 known 这半步漏了。`UnloadMagOperation` 正是外壳（ilspy：`class UnloadMagOperation : AbstractMagOperation<UnloadMagOperationDescriptor>`，ctor 带 `internalOperation`），弹夹/弹药/拆分全在内层 → known 从未登记 ⇒ "卸弹夹后变未搜索"；容器那半会下钻所以正常，正好解释"普通移动没事"。
  - 修法：`MarkOperationItemsKnown` 改外壳 + 新增递归重载（depth≤4，逐层 try/catch）+ `MarkOperationOwnItemsKnown`（读 `AbstractOperation.Items`/`IMultipleItemsOperation.Items`，**只登记物品本身不下钻**，不泄露未搜战利品）。
  - T4.2：`RestoreOperationItemsAfterResync` 的 searched 条件放宽为"`SearchedBefore` 或物品当前在本机自己库存内"，仍受 `MarkContainerSearched` 的 `ContainsUnknownItems` 闸门守。
  - 判据：`[SyncDiag] op=UnloadMagOperation … wrapped=True … known=a->b`（每局前 40 条）。
- **状态**：待实机（T4.2 的普通 MoveOperation 路径未证出缺陷，靠 `wrapped=` 与 `resync-begin` 两组日志定向）

## T5　客机 CG 进程（`UseNativeClientTrigger`）

- **要求**：**不需要延时**——`LocalTriggerDelaySeconds` 应为 0（W30 的 2f 属误改，待撤）。
- **关联补丁**：`FikaLocalCutsceneTrigger.LocalTriggerDelaySeconds`
- **本轮实现（W42）**：`FikaConfig.cs` 与字段**两处**都改回 `0f`（只改字段无效，`FikaConfig` 在初始化时硬写该值）。判据：`[SyncDiag] local-process start` 不再有 2 秒空档；`grep LocalTriggerDelaySeconds` 应只有 2 处。\n- **状态**：待实机
- **注意**：撤掉延时后 T1 的互斥问题仍然存在，两者要分开解。

## T6　服务端：无头客户端只留控制台，不启动图形界面

- **要求**：Headless（Fika.Headless）在服务端同步时**只保留控制台输出，不启动 GUI 窗口**。
- **关联位置**：服务端 `headless.display.mode` + `HeadlessLaunchHelper` + `HeadlessProfileService`；客户端 `Classes/HeadlessWindow.cs`（兜底）。
- **服务端侧落地（W40，已部署，主体）**：服务端本来就支持 `Console` 模式——`FikaConfigHeadlessDisplay.Mode` = `Console` 时 `HeadlessLaunchHelper.BuildDisplayArguments` 会给游戏追加 ` -batchmode -nographics`（Unity 用 Null 图形设备：不建窗口、不编译 shader、不提交 draw call）。本次实际改动：①部署**本线源码构建**的服务端（`FikaServer.dll 49114613…` + `FikaShared.dll 497A51AB…`，旧文件留 `.bak-redline-20261027`）；②`fika.jsonc` 的 `headless` 段加 `"display": { "mode": "Console" }`（备份 `.bak-headdisplay-20261027`）；③两个 profile 的 `HeadlessConfig.json` 补 `"Display": "Console"`（备份 `.bak-20261027`）。
- **生效方式**：**下次服务端启动**会重生成 `StartHeadless.cmd`——`GAME_ARGS` 变成 `-force-gfx-jobs native -batchmode -nographics -token=… -config=…`，`START_FLAGS` 变空（Console 不再 `start /min`）⇒ 宿主不创建任何窗口，只剩控制台与日志。
- **客户端兜底（W38）**：`HeadlessWindow.HideInsteadOfMinimize = true` → 若宿主仍是被窗口模式启起来的（比如手动启动），`ShowWindow(SW_HIDE)` 隐藏它；日志 `[Headless] Window: hid the game window …`。
- **状态**：待实机。判据：①`StartHeadless.cmd` 内出现 `-batchmode -nographics`；②宿主进程**没有窗口**（任务栏无图标）；③日志有 `Display=Console` / `hidden … visibleNow=False`

## T7　救人（复活倒地玩家）

- **要求**：**先试图修好 Fika 自己的复活**；实在修不好，才把 Bring Me To Life 的实现搬进 Fika（不是"关掉让位"）。
- **关联补丁**：`ClientReviveConfig`（W31/W33）、`ClientHealthController_Kill_Patch`、`ObservedPlayer.ToggleDowned`、`ReviveInteractable`
- **已排除**：单纯打开 `reviveConfig.enabled` 不等于可用（W31 已证明无效）；Bring Me To Life 与 Fika 复活只能二选一。
- **本轮实现（W39）找到结构性死因**：`ClientHealthController_Kill_Patch` 与 `InteractionContextHelper_GetAvailableActions_Patch` 都带 `[IgnoreAutoPatch]`，而 `PatchManager.cs:113` 会把这类补丁**直接过滤掉** → `ActiveHealthController.Kill` 从未被拦、救援菜单也从未挂上，所以 Fika 的倒地/救援**从来没有进入过执行路径**。已移除这两个属性；两个补丁都自带运行期闸门（`ReviveEnabled && CanBeDowned` / `FikaRaidGate.Skip` + 只对 `ReviveInteractable` 生效），关闭复活时行为不变。Client 构建 0 error。
- **还需一步**：`fika.jsonc` 的 `client.reviveConfig.enabled` 要改为 `true`（游戏目录文件，需你允许后才改），否则闸门仍返回放行、不会进入倒地。
- **状态**：待实机（代码侧已打通，缺开关）

## T8　破冰船 rally gate（"waiting for players N/M" 死等）

- **关联补丁**：`FikaRallyGate` + `RallyGatePatches`（W36 恢复，已部署）
- **状态**：待实机

## T9　BTR 装甲车客机只听见声音、看不见车

- **关联补丁**：`EnsureBtrViewVisible` → 原生 `ShowView()`（W28，已部署）
- **状态**：待实机

## T10　主机过场后无敌 / 客机过场结束异常

- **关联补丁**：免疫释放（W11 系列，已部署；用户实测"除 CG 进程外均未成功"）
- **状态**：未解决（W11 系列被判无效，需要重做思路）

---

## 已作废 / 待删除的补丁（不留冗余）

| 补丁 | 原因 | 处理 |
|---|---|---|
| `PoseWriteGuards` 6 处 + `Coop / Pose Write Guards` 配置项（W13/W15） | 用户实测"没有任何用" | 待删除 |
| 结局修复层三段 `WatchLocalEnding`/`PrepareLocal`/`RetryCompletion`（W18） | 用户实测"仍卡在结局界面" | 待删除后换思路 |
| `LocalTriggerDelaySeconds = 2f`（W30） | 客机 CG 进程**不需要延时** | 待改回 0f |
| Fika 复活默认关闭的"让位"策略（W33） | 用户要求先修 Fika 自己的 | 待重做（见 T7） |

## 追加区（新 bug 往这里加，衍生项加到对应项下面）

### 有意关闭的补丁（`[IgnoreAutoPatch]`，不是漏修）
| 补丁 | 关闭原因 | 何时才需要打开 |
|---|---|---|
| `FadeBlackScreen_Patch` / `StartBlackScreenShow_Patch` / `SetBlackImageAlpha_Patch` | FreeCamera 的过场淡入淡出，属表现层 | 若报告"转场没有黑屏过渡" |
| `PartyInfoPanel_CG_method_3_Patch` | 借自 Tyfon.UIFixes，本机**已装 Tyfon.UIFixes**，同时开会重复 | 卸载 Tyfon.UIFixes 后 |
| `MuzzleManager_Play_Patch` | 纯省一次 LINQ 分配；且 47888/47965 的 Il2CppInterop 桩是显式接口实现，`GetIl2CppMethodInfoPointerFieldForGeneratedMethod` 找不到字段 | 换用能解析该桩的 Interop 版本后 |
| `ItemContext_Patch` | 依赖服务端 HTTP 的物品上下文菜单 | 需要"右键发送物品"菜单时 |
| `BotStandBy_Update_Transpiler` | 无头专用（禁 bot 睡眠），改动风险大于收益 | 无头主机出现 bot 全体待机时 |

### 离线审计工具
`tools/fika-audit.ps1`（UTF-8 with BOM）→ 扫全树 5 类缺陷，报告落 `analysis/AUDIT-latest.txt`。**取代"每个 bug 跑一局"**：①`[IgnoreAutoPatch]` 未生效补丁 ②字典值形状不一致 ③同族方法递归不一致 ④每帧分配 ⑤静默早退。

---

## 2026-10-27 W48 处置（N1 / N2 / N4 同时做，Client `ADA006DF…`，仅编译）

### N1 客机在转移点互动后提示「无法使用转移功能」
- **真因（已改）**：主机 `InteractWithTransit` 只看原生 `point.Enabled`，false 就回 `InactivePoint`；合作局里按下那一刻该标志常还没被触发器置 true。
- **改法**：只要点位配置 `parameters.active`，就按“会话驱动”放行并打 `[TransitDiag] point-disabled-but-session-driven … action=accept`；被配置关掉的点仍拒（`point-disabled-by-config`）。
- **判据**：客机按下后**不再**出现“无法使用转移功能”；主机日志出现 `point-disabled-but-session-driven`。

### N4 破冰船 CG 后主机仍无敌（CG 前能受伤）
- **真因**：过场开始时本机伤害系数被压到 0，**只有**过场的原生 end actions 会恢复；免疫登记表那条是空转（`registries=0 wasImmune=0`）。
- **改法**：`RaidInteractionSync.EndCutscene`（原生 `EndCutscene` postfix，全机唯一收口）新增 `RestoreLocalDamageAfterCutscene`：本机活着、未撤离、`DamageCoeff<1` → 恢复 1。
- **判据**：主机日志 `[CgDiag] damage-coeff restored to 1 (cutscene end pid=N) was=0.00`；之后能正常受伤。

### N2 到码头后主机黑屏但可操作（卡在初始 CG）
- **真因（用户线索）**：客机在场时主机黑屏、CG 不触发；**客机退出后主机立刻正常** ⇒ 主机在等一个永远不会回来的观众确认。
- **改法（兜底）**：新增主机侧 `CutsceneStallWatchdog`（1 Hz）：广播时记 `BroadcastAt`/`BroadcastProcesses`，进程 25 秒无人推进 → 按 skip 调原生 `EndCutscene` 收尾，解掉黑屏与过场免伤。
- **判据**：主机日志 `[CgDiag] cutscene-stall-watchdog pid=N waited>=25s end=native`。
- **仍需根治**：N3 —— 客机在那张图里根本没真正进入过场（`local-process` 全 0 条），所以主机等不到确认。后续按 T1 思路：让客机在收到 `CutsceneStartPacket` 后**在该图里真的建立本地进程**。
## 2026-10-27 W49（N2 根治方向）

- **改法**：`CutsceneStartWithTimeSyncPatch` 加 Prefix —— 主机侧把**原生座位表**收窄到本机一人（地图触发器起的落地/世界类过场），原生不再等对端确认；日志 `[CgDiag] host-seats-narrowed from=N to=1`。
- **不影响**：rally/集会那类走 `StartCutscene`，座位表不变，破冰船逻辑不动。
- **判据**：主机进 Terminal 后**不再黑屏**，日志出现 `host-seats-narrowed … to=1`；若仍黑屏，看是否 `[CgDiag] cutscene-stall-watchdog` 兜底触发（25 秒内应脱困）。
- **未闭合**：广播座位表跟着变 1 人 → 客机收不到这一条；客机在 Terminal 上自己的地图触发器是否发过请求无证据（落地后 `local-process start` 0 次）。若要客机也播，需要把"广播座位表"与"原生座位表"拆开。