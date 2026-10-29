# Fika 全功能重建 —— RE 底稿与计划（2026-10-26）

> 本轮结论：**refactor 树已经≈上游 3.0.4**，不是"裁剪版"。真正的工作量是「46 个被改过的文件 + 4 个被删的文件」的逐项对账，以及用 RE 证据把 3 条已知坏链修对。峰值计价时段已暂停，恢复从本文档 §5 的 R1 开始。

## 1. 现状实测（不是估计）

| 项 | 数值 | 取证方式 |
|---|---|---|
| 上游参照 | `mods/fika-47965/release/Fika-47965-3.0.4/BepInEx/plugins/fika/Fika.Core.dll`，sha256 `21851354…`，assembly 3.0.4.0，MVID `cf1234bb…`，TypeDef 1738 / MethodDef 10814 | REA `inspect_managed_artifact`（`ev_db14768c…`，metadata complete） |
| 本线产物 | `mods/fika-47965-refactor/staging/BepInEx/plugins/fika/Fika.Core.dll`（W12，见 §4） | 同上 + 构建 |
| 源码差异 | 上游 760 个 .cs / 本线 758 个；**内容不同 46 个**；上游独有 4 个；本线独有 2 个 | 逐文件 SHA256 对比 |
| 上游独有（本线删掉的） | `Main/Utils/TransitRecovery.cs`、`Main/Utils/PoseWriteGuards.cs`、`Main/Patches/World/RallyGatePatches.cs`、`Main/Components/FikaRallyGate.cs` | 同上 |
| 本线独有（新增） | `Main/GameMode/TerminalArmory.cs`、`Main/Patches/TransitController/TransitPoint_SetEnabled_Patch.cs` | 同上 |
| 被改的 46 个文件按目录 | Utils 7、Components 7、Networking 6、GameMode 6、ClientClasses 5、HostClasses 3、根 3、Patches/World 3、其余各 1 | 同上 |

被改文件里与三条坏链直接相关的：
`RaidInteractionSync.cs`、`FikaLocalCutsceneTrigger.cs`、`RaidInteractionPatches.cs`、`FikaHostTransitController.cs`、`ClientTransitController.cs`、`ClientInventoryController.cs`、`ClientInventoryOperationHandler.cs`、`CutsceneBoundary.cs`、`CoopGame.cs`。

## 2. RE 证据（原生契约，已落地存档）

工具链：
- 托管面：`tools/Ildump`（`<dll> <out> 'Type#sig'`）→ 精确的 `NativeMethodInfoPtr_*` 命名（名字里带完整参数类型）与 `NativeFieldOffset_*`。
- 原生面：`GameAssembly.dll`（100.4 MB）+ `EscapeFromTarkov_Data/il2cpp_data/Metadata/global-metadata.dat`（30.6 MB）。**注意**：本机没有 Il2CppDumper 产出的 RVA 映射表，Ghidra 直接开 GameAssembly 拿不到方法名；要深入原生语义需先补一份 name→RVA 映射。
- REA 对 `Assembly-CSharp.dll`（62.7 MB，metadata 45.4 MB）**无法收纳**（超 64 MiB 表示预算）→ 该程序集一律走 Ildump；REA 用于 Fika 自身程序集（≤3.5 MB，metadata 可收纳）与未来的原生分析。

已确认的原生契约（存档：`analysis/_re-transit-sig.txt`）：

**TransitPoint（`EFT.Interactive.TransitPoint`）** —— 三个状态位，之前只用一个：
- `_Enabled_k__BackingField`(bool) + `get/set_Enabled`：**地图触发器写的那一位**；
- `_IsActive_k__BackingField`(bool) + `get/set_IsActive`，由私有 `UpdateActivation()`/`Update()` 重算；
- `parameters`（`TransitParameters`，含 `.active`）来自 location settings 的静态配置；
- 另有 `_confirmedPlayers`、`_playerGroups`、`_groupTimers`、`_groupSize`、`GroupEnter/GroupExit`、`IsConfirmed/SetConfirmed`、`Transit()`（私有 Task）、`UpdateGroupTimers()`。
- 实现 `IPhysicsTrigger`（显式接口属性 `enabled`）。

**EFT.GameTriggers.HandlerTransitPointState** —— 字段只有 `_transitPoint`、`_targetState`，方法 `Start()`、`OnTrigger(TriggerEvent)`。即：**点位的 Enabled 由地图触发器写，写成 `_targetState`**。

**EFT.GameTriggers.HandlerTransitTimeWindow** —— `_timeWindowTransitRequirement`、`_outputOnPassTriggerId/_outputOnFailedTriggerId`、`EmitPass(int)`、`NotifyFail(int, string)`、`HasAuthority()`。即：时间窗是独立触发器，能"通过/失败"并再触发别的 trigger。

**EFT.LocalTransitController** —— `InteractWithTransit(Player, InteractWithTransitPacket)`、`PlayerEnter/PlayerExit(TransitPoint, Player)`、`Sizes(int, Dictionary<int,byte>)`、`Timers(int, Dictionary<int,ushort>)`、`Transit(TransitPoint,int,string,Dictionary<string,ProfileKey>,Player)`。

**客机侧 `ClientTransitController.ApplySnapshot`（本线 `ClientTransitController.cs:215`）**：`SetPointEnabled(entry.Key, enabled.Contains(entry.Key))` —— 快照里没列到的点会被**写成 false**；`SetPointEnabled` 内部有 `if (point.Enabled == enabled) return false;` 再写。这解释了"客户端 point-apply 4 条 true"与"其它点被关"。

**主机侧 `FikaHostTransitController.CreateInitialization`（FikaHostTransitController.cs 约 3275）**：`EnabledPointIds` 直接取自主机本地 `entry.Value.Enabled`。因此客机日志里 `point-apply enabled=true` **等于证明主机快照时刻这 4 个点 Enabled=true**。

## 3. 由此得到的转移链判定（下一步只需一行日志）

现象矛盾：客机快照说 point 25 enabled=true，主机却在 `InteractWithTransit` 回了 `InactivePoint`，而 `point-broadcast=0` 说明主机运行期没有任何 Fika 写入。

W12 把主机拒绝行扩成三态：
```
[TransitDiag] host answer=InactivePoint point=25 profile=… raidId=… reason=… sent=yes
    pointState=enabled=no,isActive=yes,configured=yes zone=… registered=…
```
判读表：
| pointState | 结论 | 修法方向 |
|---|---|---|
| `enabled=no` | 地图触发器（`HandlerTransitPointState`）把它关了，或从未开过 | 追触发器的 `_targetState`；主机不要再用 Enabled 当唯一闸门 |
| `enabled=yes,isActive=no` | 点位自身 `UpdateActivation` 判不可用 | 查 `_playerGroups/_confirmedPlayers/时间窗` |
| `enabled=yes,isActive=yes` 且仍 InactivePoint | 走的是 access / entry-requirements 分支 | 看 `reason=` 值 |

## 4. 本轮已落地的改动（W12）

- `FikaHostTransitController.SafePointEnabled` → 输出 `enabled/isActive/configured` 三态（RE 依据见 §2），日志字段名改为 `pointState=`。
- 构建：Client 0 error（174 警告，均为既有 CA1416/CS0169 一类）。
- 产物哈希与本包见 `release/Fika-47965-refactor-20261026-W12-re.txt` 与旁边 `.sha256`。

## 5. 恢复点（下一个谷期从这里继续）

R1. 取一次双人实机（或让用户只发主机那行 `[TransitDiag] host answer=`）→ 按 §3 判读表决定转移链的修法；**不要先放宽闸门**。
R2. `46 个被改文件`逐项对账：按 `REFACTOR-TRACKER.md` 的变更登记，把"改了但理由已不成立"的恢复成上游 3.0.4；把"上游也错、本线修对了"的保留并补注释。建议按目录分批（Utils → Components → GameMode → Client/HostClasses → Networking）。
R3. 4 个被删文件的功能评估：`FikaRallyGate`+`RallyGatePatches`（集合点防绕锁/跳过投票）、`PoseWriteGuards`（姿态写入性能守卫）、`TransitRecovery`（转移恢复开关）——按"是否还需要"逐个决定恢复或正式注销。
R4. 逐功能验收表（双人）：CG/过场、转移、物品与背包 known/searched、哨兵权限、掉落与尸体/BTR 同步、语音、结局与撤离。
R5. 全部通过后删诊断行（清单见 HANDOFF §9.5）。

## 6. 环境注意

- 构建时若 MSBuild 报 `CreateTempSubdirectory / ClearCacheDirectory` 访问被拒：把 `TEMP`/`TMP` 指向工作区（如 `.build\tmp`）再跑，已在本次验证有效。
- 本机 `SPT.Server.exe` 仍会在 `RagfairServer.Load → PaymentHelper.IsMoneyTpl` 处以 HashSet 并发损坏崩溃（与 Fika 无关），本机只能当客机。

## 7. R2 对账清单（46 个内容不同的文件，2026-10-26 实测）

列说明：up=上游行数，rf=本线行数，delta=本线-上游，trk=该文件名在 REFACTOR-TRACKER.md 里出现的次数（0 = 改动没登记）。

| up | rf | delta | trk | 文件 |
|---:|---:|---:|---:|---|| 2813 | 260 | -2553 | 3 | `\Main\Components\CutsceneBoundary.cs` |
| 731 | 456 | -275 | 1 | `\Main\Components\FikaExtractDiag.cs` |
| 291 | 63 | -228 | 1 | `\Main\Utils\NvgDiagnostics.cs` |
| 3669 | 3508 | -161 | 1 | `\Main\HostClasses\FikaHostTransitController.cs` |
| 710 | 577 | -133 | 1 | `\Main\Patches\World\PlantInteractionDiagPatch.cs` |
| 2257 | 2156 | -101 | 1 | `\Main\GameMode\HostGameController.cs` |
| 1782 | 1683 | -99 | 1 | `\Main\GameMode\BaseGameController.cs` |
| 2191 | 2122 | -69 | 3 | `\FikaConfig.cs` |
| 423 | 397 | -26 | 1 | `\Main\Utils\TransitReloadGate.cs` |
| 280 | 259 | -21 | 1 | `\Main\GameMode\CountdownBisect.cs` |
| 156 | 136 | -20 | 0 | `\Networking\FikaServer.RaidInteractions.cs` |
| 1091 | 1074 | -17 | 1 | `\Main\Components\FikaFinalMissionDirector.cs` |
| 270 | 254 | -16 | 0 | `\Main\GameMode\CountdownRestore.cs` |
| 396 | 381 | -15 | 0 | `\Main\Utils\NativeEntryWarmup.cs` |
| 908 | 894 | -14 | 2 | `\Main\Components\CoopHandler.cs` |
| 953 | 942 | -11 | 0 | `\Networking\FikaClient.Callbacks.cs` |
| 1137 | 1126 | -11 | 1 | `\Networking\FikaClient.cs` |
| 455 | 445 | -10 | 0 | `\Networking\FikaClient.MapState.cs` |
| 581 | 573 | -8 | 0 | `\UI\Custom\LoadingScreenUI.cs` |
| 773 | 765 | -8 | 0 | `\Main\GameMode\ClientGameController.cs` |
| 456 | 449 | -7 | 0 | `\Networking\NetManagerUtils.cs` |
| 161 | 154 | -7 | 0 | `\Main\Utils\SessionPump.cs` |
| 272 | 266 | -6 | 0 | `\Main\ClientClasses\FikaClientGameWorld.cs` |
| 739 | 733 | -6 | 0 | `\Main\Utils\FikaRoutinePump.cs` |
| 686 | 681 | -5 | 0 | `\Main\Patches\LocalGame\TarkovApplication_LocalGameCreator_Patch.cs` |
| 520 | 516 | -4 | 0 | `\FikaPlugin.cs` |
| 333 | 329 | -4 | 0 | `\Main\HostClasses\FikaHostGameWorld.cs` |
| 640 | 636 | -4 | 0 | `\Main\Components\FikaExfilManager.cs` |
| 2338 | 2336 | -2 | 3 | `\Main\GameMode\CoopGame.cs` |
| 280 | 278 | -2 | 1 | `\Main\ClientClasses\FikaClientWorld.cs` |
| 238 | 236 | -2 | 0 | `\Main\HostClasses\FikaHostWorld.cs` |
| 95 | 94 | -1 | 0 | `\FikaPluginBehaviour.cs` |
| 401 | 400 | -1 | 0 | `\Main\ObservedClasses\ObservedMovementContext.cs` |
| 2768 | 2768 | 0 | 0 | `\Main\Players\ObservedPlayer.cs` |
| 136 | 137 | 1 | 1 | `\Main\Utils\LoadSync.cs` |
| 97 | 98 | 1 | 1 | `\Main\Utils\KeypadDiag.cs` |
| 277 | 281 | 4 | 1 | `\Networking\FikaClient.RaidInteractions.cs` |
| 432 | 443 | 11 | 0 | `\Main\Patches\TransitPresence\FikaTransitClientPresence_Patches.cs` |
| 161 | 195 | 34 | 0 | `\Main\Patches\TransitController\FikaWorldController_Patches.cs` |
| 2030 | 2073 | 43 | 2 | `\Main\ClientClasses\ClientTransitController.cs` |
| 451 | 519 | 68 | 0 | `\Main\ClientClasses\ClientInventoryOperationHandler.cs` |
| 440 | 516 | 76 | 1 | `\Main\Patches\World\RaidInteractionPatches.cs` |
| 199 | 341 | 142 | 1 | `\Main\Patches\World\SentryTerminalAccessPatches.cs` |
| 1194 | 1339 | 145 | 4 | `\Main\Components\RaidInteractionSync.cs` |
| 240 | 407 | 167 | 0 | `\Main\Components\FikaLocalCutsceneTrigger.cs` |
| 705 | 1473 | 768 | 0 | `\Main\ClientClasses\ClientInventoryController.cs` |

**从表里读出的三个重点：**

1. `CutsceneBoundary.cs` 2813→260（-2553）：A01 整体重写，是全树最大漂移点，恢复或替代必须单独评估（现在只剩建控制器 + 链 bank）。
2. `ClientInventoryController.cs` 706→1473（+768）：W8/W10 的 known/searched 与 resync 机制全堆在这里，其中 `MarkOperationItemsKnown` 直到 W11 才接上调用者——这是"物品未检视"反复修不好的结构性原因。
3. 约 20 个文件在 REFACTOR-TRACKER.md 里 0 次命中（如 `Networking/FikaClient.MapState.cs`、`Main/Utils/NativeEntryWarmup.cs`、`UI/Custom/LoadingScreenUI.cs`、`Main/GameMode/CountdownRestore.cs`）：这些改动没留下理由，对账时只能靠上游与本线的二元对比逐段判断。
## 8. R3 结论：4 个上游独有文件的去留（2026-10-26 读完源码后定）

| 文件 | 行数 / 上游引用 | 它解决什么 | 去留决定 |
|---|---:|---|---|
| `Main/Utils/PoseWriteGuards.cs` | 17 / 11 | 观察者姿态写入的「值未变就不写」守卫。原生 setter（`MovementContext.SmoothedCharacterMovementSpeed`、`SprintSpeed`）每次调用都推 animator；移除后 61 个观察者每帧约 500 次无条件原生写 | **恢复**（最高优先）。纯性能、无行为分支，改动点 = `ObservedPlayer` 5 处 + `ObservedMovementContext.SetCharacterMovementSpeed` 1 处，照上游逐处套回并编译 |
| `Main/Components/FikaRallyGate.cs` + `Main/Patches/World/RallyGatePatches.cs` | 368 + 156 | 破冰船集合点门：原生 `TriggerRallyZone.GetAliveHumanPlayerCount()` 数出来的 N 不等于本局真人玩家数，门会等一个到不了的数（客户端永远显示 waiting N/M） | **先验证再决定**。若破冰船集合点在没有它时仍卡住 → 整份回迁上游两文件；若正常 → 保持删除（W3-rally 的重置有效）。验证项并入 R4 |
| `Main/Utils/TransitRecovery.cs` | 143 / 41 | ①不经 `Coop/Diagnostics` 开关也能读的判据行；②被拒一次后的受限恢复 | **保持删除**。诊断已由 W8–W12 的 `[TransitDiag]` 直接输出；回迁会带回 12 个 ConfigEntry 与悬空 `SettingChanged`。若将来要「被拒后自动重试一次」，写一个 ≤40 行的最小实现，不要整份回迁 |

**由此得到的 R3 执行顺序**：先做 `PoseWriteGuards` 恢复（小、可编译验证、收益是帧率），再在 R4 里带一项「破冰船集合点门」的验收，用它决定 rally 两文件的去留。

**本轮（峰值前）实际执行**：只完成 R3 判定与本节记录，未改代码；`PoseWriteGuards` 的 6 个改动点留到下一个空闲窗口（18:00 之后）执行。
### 8.1 执行记录（2026-10-26 峰值前 20 分钟窗口）

- **已做**：`PoseWriteGuards` 已恢复（文件 + 6 个守卫点位），Client 构建 0 error，client `240D6928…`，包 `W13-poseguards.zip`（52 条目），本机已部署。
- **为什么先做它**：纯性能、无行为分支、可编译验证；上游 11 处引用里只有 6 处是写入点位，其余是 FikaConfig 的 ConfigEntry（本轮未接回）。
- **下一窗口（18:00 之后）从 R1 继续**：取主机 `[TransitDiag] host answer=… pointState=…` 一行 → 按 §3 判读表定转移修法。
### 8.2 R2 抽样结论（2026-10-26 第二轮，仅读不写）

对 3 个"0/低 tracker 命中"的文件做了行级对账，结论都是**纯诊断删除、无行为漂移**：

| 文件 | 差异 | 性质 |
|---|---:|---|
| `Networking/FikaClient.MapState.cs` | 10 处（全为上游独有） | 全是 `LoadSync.Trace/Report` 的换世界转储，无逻辑分支 |
| `Main/Utils/NativeEntryWarmup.cs` | 15 处（全为上游独有） | 一个 `Aggregate(List<string>)` 汇总助手 + 两个空块；无调用点语义变化 |
| `Main/Components/CoopHandler.cs` | 14 处（全为上游独有） | 3 组 `LoadSync.Report`（CleanUpForTransit / Update 队列生成 / QueueProfile） |
| `Networking/FikaClient.cs` | 11 处（全为上游独有） | 3 组 `LoadSync.Report`（世界包队列 arm/flush），无逻辑分支 |

→ 网络层与基础工具层**没有行为漂移**，R2 的风险面收敛到少数行为文件：`Components/CutsceneBoundary.cs`(2813→260)、`ClientClasses/ClientInventoryController.cs`(+768)、`ClientClasses/ClientTransitController.cs`(+43)、`Components/RaidInteractionSync.cs`(+145)、`Components/FikaLocalCutsceneTrigger.cs`(+167)、`HostClasses/FikaHostTransitController.cs`(-161)、`GameMode/HostGameController.cs`(-101)/`BaseGameController.cs`(-99)。

**下一窗口的对账顺序（按"行为风险 × 已知症状"排）**：CutsceneBoundary → ClientInventoryController → RaidInteractionSync → FikaLocalCutsceneTrigger → ClientTransitController → HostClasses → GameMode。
### 8.3 CutsceneBoundary 的丢弃面（R2 第一优先项的量化）

上游 79 个成员 → 本线 12 个（新增 4 个：`LinkBank`/`PumpClientOnce`/`SceneSample`/`TickPlayback`），**丢 71 个**。按功能归类：

| 组 | 被丢弃的成员 | 影响 |
|---|---|---|
| 跳过投票 / 提示 UI | `PumpSkipOverlay`、`DismissSkipPrompt`、`SampleSkipPrompt`、`ReadSkipFlag`、`ActionCounts` | 客机/主机看 CG 时的"跳过"提示与投票链没了 |
| 接管 / 链接 | `PumpTakeover`、`TryAdoptLink`、`TryInvoke`、`Link`、`LinkState`、`BankLinks` | 只有 `LinkBank` 保留，接管式链路消失 |
| 播放修复 / 救援 | `TryRepairHostPlayback`、`TryRescueLocalScene`、`TryStopCutscene`、`ForceEnd`、`ForceStopLocal`、`RescueState`、`NativeEngineTick` | 过场卡住时没有自救路径 |
| 诊断 / 观测 | `Describe`、`Explain`、`Report*`（6 个）、`Sample*`、`Scene*` 查询器 | 过场问题无法从日志定位 |
| 本地播放泵 | `PumpLocalPlayback`、`LocalPlayback`、`LocalState`、`TryShowTimeline`、`TickDelta`、`TimeScale` | 本地播放交给别处（`CutsceneBoundary.TickPlayback` 只剩一次性 tick） |

**下一窗口的处置建议**：不要逐成员补。以"上游 `CutsceneBoundary.cs` 整份回迁"为基线，然后把本线唯一真正的意图变更（A01：不接管控制器、只建控制器 + 链 bank）作为差异重新实现一遍；判据是 ①CG 能播能结束（已有）②跳过提示/投票可用 ③过场卡住能自救。若回迁后编译冲突过多，再退化为"按上表分组逐个回补"。
### 8.4 对 §8.3 的更正（2026-10-26 第五轮，重要）

§8.3 按「文件成员数」判定 CutsceneBoundary 丢了 71 个能力，这个读法是错的：**能力是搬家，不是丢失**。本轮在全树核对，被列进"丢失"的那些机制在本线别处都在：

| §8.3 判为"丢失" | 实际在本线哪里 |
|---|---|
| 跳过投票 `EndIfEveryViewerVoted` | `RaidInteractionSync.cs:1180`（投票/超时两路），入口 `OnSkipIntention`(1099) → `HandleSkip`(1124) |
| 客机投票上行、主机广播 | `CutsceneSkipIntentionPacket` + `FikaServer.RaidInteractions.cs:97` 收包 + `FikaClient.RaidInteractions.cs:220` 应用 |
| 卡住自救（主机） | `RaidInteractionSync.SkipWatchdog()`(1197)：每秒一次，`EndIfEveryViewerVoted(...,"timeout")` + `CloseSilentViewers()`（用原生 `PlayerViewCutsceneComplete` 把沉默观众计为完成） |
| 卡住自救（客机） | `FikaLocalCutsceneTrigger.PumpEnds/EndLocal`（PendingEnds + 超时 + 原生 `EndCutscene`） |
| 客机本地播放 | `PlayLocalNativeProcess` + `FikaLocalCutsceneTrigger.NoteLocalProcess` |

同时 `RaidInteractionSync` 相对上游是**超集**（新增 7 个成员：`FilterViewersToSession`/`StartCutsceneFromWithTime`/`IsCutsceneBroadcast`/`NoteLocalTimelineShown`/`ReleaseLocalSniperImmunity`/`ReleaseSniperImmunity`/`CountOf`；只丢了 2 个诊断助手），`FikaLocalCutsceneTrigger` 也是超集（+3：`EndLocal`/`NoteViewed`/`PumpEnds`）。

**方法论教训**：成员级 diff 只能说明"这个文件里有没有这个名字"，不能说明"能力在不在"。判定能力缺失必须在**全树**里按语义找（投票、跳过、超时、自救各自的入口与副作用），否则会把"搬家"误判成"丢失"，进而做出一次毫无必要的整份回迁。

**修正后的 CutsceneBoundary 结论**：它现在只剩「建控制器 + 链 bank + 一次性 tick」，其余由上述三处承担；因此下一步不是回迁，而是**在真机上验证那三条能力是否真的按设计工作**（跳过提示是否出现、沉默观众超时是否收尾、客机本地结束是否 end=native），再决定要不要补 `CutsceneBoundary` 的残余职责（如 `Sample*`/`Report*` 观测面）。
## 9. 客机专属 bug 审计（2026-10-26 起，用户追加要求）

### 9.1 审计面（实测）

`IsClient` 相关判断 **81 处 / 38 个文件**（不含注释行）。按文件 Top：`FikaPlayer.cs` 13、`RaidInteractionSync.cs` 6、`FikaFinalMissionDirector.cs` 6、`CoopHandler.cs` 5、`ClientQuestController.cs` 5、`RaidInteractionPatches.cs` 4、`FikaClientFirearmController.cs` 4、`FikaFlareExfil.cs` 3、`FikaClientGrenadeController.cs` 3。

方法：对每个 `IsClient` 分支问三件事——①主机有没有对应的一半；②客机这条路径失败时有没有日志/回退；③它写的状态有没有上行/下行同步。只改能给出"错在哪"的分支。

### 9.2 第一条线索：`StrictSync` 门控的 13 条客机专属路径

`FikaPlayer.cs` 975–1171 有 **13 处** `FikaBackendUtils.IsClient && _baseInventoryController.StrictSync`，它们决定客机的手部控制器切换是"发请求等主机确认"（`SendCallbackRequest`）还是"本地先做、只通报"（`SendPacket`）。这条差异正好是"客机专属手感/同步 bug"的典型来源（切枪、吃喝、治疗在主机眼里的时序）。

**事实更正**：`StrictInventorySync` 在本线**存在**（`FikaConfig.cs:240` 属性、`:1562` SetupSetting），并且 `CoopGame.cs:343` 把它传给 `FikaPlayer.Create(..., FikaSingleton<IFikaNetworkManager>.Instance.StrictInventorySync)`。所以这 13 条不是死代码（此前记录成"已被删除"是错的）。

下一步（下一窗口）：读 `:1562` 的默认值与 `IFikaNetworkManager.StrictInventorySync` 的取值链，确认默认是否 true；若 true，则逐条核这 13 处的"确认/通报"语义是否与主机的处理对称（主机侧 `HostInventoryController` 的对应分支）。

### 9.3 其余候选（按用户报过的客机症状排）

| 症状 | 客机侧代码位置 | 状态 |
|---|---|---|
| CG 座位数显示 1/4、1/2 | `FikaLocalCutsceneTrigger.Fire`（viewers 取 `AllAlivePlayersList`）vs `GrantNativeStart`（应该裁到 1/1） | 待核：grant 后原生是否真按 1/1 走 |
| 过场结束 NRE | `EndLocal` → 原生 `EndCutscene` | W11 已修（复制 viewers），待实机确认 `end=native` |
| 物品"未检视" | `ClientInventoryOperationHandler` 成功路径 + `ClientInventoryController.ApplyRelayedOperationBookkeeping` | W11 已接上，待实机看 `known=a->b` |
| 客机过场后无敌/受伤 | `RaidInteractionSync.EndCutscene` + `TimelineShowed` 兜底 | W11 已改，待实机 |
| 刷卡上行 | `Player_ExecuteInteraction` postfix → `NoteNativeEmission` | 已有 1 次 `swipe-report` 证据 |
| 跳蚤市场堆叠异常 | SPT 服务端 ragfair（与本线无关） | 环境问题 |

### 9.4 纪律

客机专属 bug 与主机 bug 一样，先拿"哪一侧、哪一步"的证据再改；本轮只做静态审计与最小修复，不为了让界面好看而放宽任何闸门。
### 9.5 §9.2 的核实结果（2026-10-26 第六轮）

**取值链已确认**：`FikaConfig.cs:1562` `SetupSetting("Gameplay","Strict Inventory Sync", true, …)` → 默认 **true**；主机 `FikaServer.cs:195` 取自己配置，经 `FikaServer.Callbacks.cs:445`（`StrictSync = StrictInventorySync`）下发，客机在 `FikaClient.Callbacks.cs:532` 存进 `IFikaNetworkManager.StrictInventorySync`，`CoopGame.cs:343` 再传进 `FikaPlayer.Create`。**结论：客机的严格同步由主机的配置决定（不是客机自己的），默认 true → 那 13 条 `IsClient && StrictSync` 路径是活跃路径。**

**但这条线上没有"卡死"bug**：`FikaPlayer` 内有 `_proceedCallbacks` + `_proceedCallbackTimes`，并有 `ExpireStaleProceedCallbacks(maxAgeSeconds)`（注释写明由 hands watchdog 调用，丢弃回答已迟到的 proceed 回调），`WaitingForCallback` 依赖的那张表因此不会永久非空。也就是说"主机不回答 → 客机永远不能开枪"这条已由上游设计兜住。

**因此 §9.2 不再列为待修问题**。仍值得做的是：`SendCallbackRequest` 与主机 `HostInventoryController` 侧的处理是否**逐类型对称**（13 处覆盖的控制器：空手/食物/医疗/枪械/手雷等）——如果某个类型客机发请求、主机不处理，就会每次都要等 watchdog 超时才恢复（表现为"客机切这类东西总是卡一下"）。这是下一窗口可静态核完的项：对 13 个 `SendCallbackRequest` 类型逐一找主机的 `HandleCallbackRequest`/`SendCallbackResponse` 分支。
### 9.6 客机专属项的静态核实（2026-10-26 第七轮，两项结案 + 一项新观察）

**① 严格同步的"请求/应答对称性"——结案：无 bug**
11 个 `SendCallbackRequest` 与主机 `OnProceedRequestPacketReceived`（`FikaServer.Callbacks.cs:179`）逐一核对：

| 类型 | 客户端带 ItemId | 主机处理 |
|---|---|---|
| EmptyHands | 无 | 专门分支"总是接受"，先于找物品返回 |
| Weapon / UsableItem×2 / QuickUse / MedsClass / FoodClass / Knife / QuickKnifeKick / GrenadeClass / QuickGrenadeThrow | 全部携带（`Weapon.Id` / `_item.Id` / `_meds.Id` / `_foodDrink.Id` / `Knife.Item.Id` / `_throwWeap.Id`） | 统一走 `TryFindItemForProceedPacket(packet.ItemId)` + `item.CheckAction` 后应答 |

上游同文件完全一致（11/11 对齐），所以这条链没有"发了没人应"的类型缺口。

**② 客机 CG 座位数 1/4——结案：当前树已按 1/1 设计**
`FikaLocalCutsceneTrigger.SingleLocalViewer = true`，且 `GrantNativeStart` 把调用方的 viewers 列表**原地**裁成只剩本机 playerId（`viewers.Clear(); viewers.Add(local);`），原生 body 读的就是这张表 → 计数器应为 1/1。用户报的 1/4 属于 W9 那一版（单座椅表 + 无结束逻辑已被 W9-revert 撤掉），当前状态不是那个形状；仍需一次实机确认。

**③ 新观察（未改，记账待验）：`LocalTriggerDelaySeconds = 0f`**
上游是 2f（先等主机派发，等不到再本地自起）。本线改成 0 → 客机**立即**起自己的本地 CG，可能比主机的短一截 RTT，属于"客机 CG 与主机轻微不同步 / 声音画面错位"的候选原因。暂不动：现有证据（客机 CG 能播能结束）不支持把它当 bug，等实机确认是否真有错位再定（改回 2f 是一行、可随时回退）。
## 10. R2 抓到的第一个真回归（2026-10-26 第八轮）

**`HostGameController.CreateBot` 的串行化门被本线删掉了。**
- 上游（`mods/fika-47965/source/.../HostGameController.cs` L152/L314-333）：`private Task _botCreateTail`，`CreateBot` 只做门、实体在 `CreateBotCore`；注释写明"Interchange 一帧激活 12 波中的 10 波 → 十个创建并发 → 在原生 `Player.Create` 里约第 10 个 bot 崩掉整局"。
- 本线：`_botCreateTail` 0 命中、`BossWaveActivationSpacingMilliseconds` 0 命中 → 门和间隔都没了，其余 28 行新增是别的东西。
- 本轮恢复：字段 + `CreateBot` 包装（`TaskCompletionSource` 串行，`await previous` 后再跑 `CreateBotCore`，`finally` 里 `SetResult`），原方法体改名 `CreateBotCore`。
- **未恢复**：`OnBossWaveActivated` 里的 `MainThreadDispatcher.RunAsync + Task.Delay(50ms)` 间隔——它防的是"同一帧堆十个激活"的上游问题，属优化，下一步单独做。

这条说明 §7 表格里 `HostGameController.cs -101 行`不是"删掉诊断"，而是**丢了崩溃修复**。R2 剩下的 6 个行为文件必须同样按"删的是不是只有日志"逐块核，不能按行数判断。
## 11. R2 逐块核：`ClientTransitController.cs`（2026-10-26 第十轮）——无机制丢失

上游独有 31 行、本线独有 74 行，逐块看过后，本线的差异只有两类，**都不是丢功能**：

1. **开关内联成默认值**：上游 `TransitRecovery.*`（W1c 已整类删除）在这些位置变成了字面常量。逐个核对常量与上游默认一致（全部为"开"）：
   `if (true && wasShowing)`＝`HideCountdownWithoutOwnEntry` 开；`if (!true && !true)`＝两个倒计时行为都开（不早退）；`if (!true) return <passive>`＝`ActivateTransitTimerPanel` 开；`!true || params == null`＝`ClientEnsureTransferStash` 开；`RearmAfterRefusal`/`ReassertPresenceOnRefusal`/`PressFallback`/`PressFallbackNativeExit` 同为开。**没有"该开的被内联成关"的情况**。
2. **`MirrorPointEnabled`（只开不关）→ `SetPointEnabled`（双向）**：上游 `_enabledMirrored` + 只写 true 的镜像，本线换成带 `point.Enabled == enabled` 早退的双向写，Show/Confirm/CheckAccess/Extract 四个调用点仍然传 true。语义等价或更强（W10 的点位双向同步就建立在这上面）。

顺带记录（**不是 bug，是清理项**）：内联后留下的 `if (true)` / `if (!true)` / `if (!true && !true)` 共 8 处死分支，读代码时会误导；等所有对账做完统一清。

**结论**：`ClientTransitController` 可以移出"待核"名单；转移链的未知仍在**主机侧**运行期状态（`point.Enabled` / `IsActive` / 闸门），而不是客机应用路径。
## 12. R2 行为文件审计收官（2026-10-26 第十一轮）

| 文件 | 上游独有行 | 本线独有行 | 判定 |
|---|---:|---:|---|
| `ClientClasses/ClientInventoryController.cs` | **0** | 768 | **纯超集**——一行上游代码都没删，W8/W10 的 resync/known 机制全叠在上面。没有"丢功能"，物品问题只能出在新机制本身或原生行为差异 |
| `Components/RaidInteractionSync.cs` | 2（诊断助手） | 145 | 超集（+7 成员），无丢失 |
| `Components/FikaLocalCutsceneTrigger.cs` | 0 | 167 | 超集（+3 成员），无丢失 |
| `ClientClasses/ClientTransitController.cs` | 31 | 74 | 等价改造（开关内联成默认值 + `MirrorPointEnabled`→双向 `SetPointEnabled`），8 处 `if (true)` 死分支待清，无丢失 |
| `HostClasses/FikaHostTransitController.cs` | 待核 | — | 本线删除的多为兜底/诊断（W3-transit / W8-revert），属于主动重置 |
| `GameMode/HostGameController.cs` | 129 | 28 | **真回归已修**：bot 创建串行化（W16）+ 波次 50ms 间隔（W17） |
| `GameMode/BaseGameController.cs` | 110（87 诊断） | 11 | 结构重排：进局序列的调用全部仍在别处（`FinalCountdownScreenController`×6、`AmbientAudioSystem`×3、`FadeInVolumeBeforeRaid`×2、`TimerPanel.ProfileId`×8、`GameUi.SetActive`×7），无丢失 |
| `Components/CutsceneBoundary.cs` | 71 成员 | 4 | 能力搬家（§8.4 更正） |

**R2 结论**：7 个行为文件里只有 `HostGameController` 真的丢了机制（已修复）；其余是超集、等价改造或结构重排。**"重建所有功能"里"功能被删掉"这一类风险基本排除**——剩下的都是"机制在但行为不对"，必须靠实机日志定位，而不是继续读代码。

后续清单（按证据成本排）：
1. 主机 `point-broadcast` / `point-broadcast-skipped reason=` 一行 → 转移（零成本，只要一次实机）；
2. `[SyncDiag] op=… bookkeeping known=a->b` → 物品未检视（同上）；
3. `local-process end … end=native` + `cutscene-immunity-release` → 过场/免疫（同上）；
4. 纯代码可做的剩余项：8 处死分支清理、`LocalTriggerDelaySeconds` 0→2f 的 A/B（各一行）。
## 13. 功能验收矩阵（2026-10-26 第十二轮，R4 的底座）

代码面：14 个功能区**全部有实现**，没有整块缺失（按关键字统计：语音 430 处/31 文件、BTR 304/40、尸体掉落 666/64、空投 251/26、手雷投掷 1166/215、信号弹 499/21、复活医疗 856/77、哨兵终端 799/36、转移撤离 2587/120、过场结局 1450/104、任务对话 2015/183、HUD 235/40、弹药弹匣 1119/87、护甲弹道 835/107）。

| 功能区 | 代码 | 静态核验 | 实机证据 |
|---|---|---|---|
| 过场 / CG | ✅ | 客户端生命周期完整（超集）；跳过投票、沉默观众收尾、本地自救都在 `RaidInteractionSync`/`FikaLocalCutsceneTrigger` | 部分：客机 CG 能播能结束（20:57 日志）；跳过/自救未验 |
| 终点站转移 | ✅ | 客机应用路径已核（§11）；主机侧运行期状态未定 | 部分：客机 point-apply 有、主机拒绝原因未知 |
| 物品与背包 | ✅ | `ClientInventoryController` 为纯超集（0 行删除）+ W11 已接 known/searched | **无**（W11 起从未实跑） |
| 哨兵 / 刷卡 | ✅ | 上行链路在（postfix → NoteNativeEmission → packet → host Apply） | 部分：`swipe-report` 1 次 |
| 掉落 / 尸体 | ✅ | — | **无** |
| BTR | ✅ | — | **无** |
| 空投 | ✅ | — | **无** |
| 手雷 / 投掷 | ✅ | — | **无** |
| 信号弹 / 夜视 | ✅ | — | **无** |
| 复活 / 医疗 | ✅ | — | **无** |
| 任务 / 对话 | ✅ | — | **无** |
| 语音 VOIP | ✅ | — | **无** |
| 结局 / 撤离 | ✅ | ⚠️ 见下 | **无** |
| HUD / UI | ✅ | — | **无** |

### 13.1 新发现：结局修复被"断线"（不是开关，是删了代码）

`FikaFinalMissionDirector.EndingRepair` 字段本线默认 `false`（配置项 `FinalMissionEndingRepair` 默认 true 并在 SetupConfig 里绑回，所以运行时是 true），**但上游受该开关保护的两段逻辑在本线被整段删除**：
- 上游 L300 `if (EndingRepair && _localStarted && !_localComplete) WatchLocalEnding();`（本机结局卡住时的看门狗）
- 上游 L305 `if (EndingRepair && _localComplete && !_exitAuthorized) …`（本机看完结局后的撤离授权）

这是 W3-finalmission「结局导演修复层关闭并断线」的后果：**开关还在、被它保护的行为不在**。用户症状层面会是"看完结局不能撤离/结局卡住"。处置建议：从上游把这两段连上下文取回，用 `EndingRepair` 正常门控，然后专门验一次撤离；本轮不动（需要完整读 `WatchLocalEnding` 与 `_exitAuthorized` 的依赖）。
### 13.2 协议层接线审计（2026-10-26 第十八轮，可复用探针）

做法：扫 `Networking/` 下所有 `public struct|class *Packet`，再数每个类型在**全树**的出现次数；只出现在定义文件里的就是"没有任何收发方"的包。

结果：57 个 packet 类型，**55 个在用**；仅两个无使用者——`AirdropUpdatePacket`、`CorpsePositionPacket`。而这两个在**上游 3.0.4 里同样只有 1 处（定义）**，属历史遗留死类型，不是本线删连接的后果，所以不动（保持与上游一致）。

意义：协议层没有"本线特有"的断线。空投/尸体这些"零证据"功能区不是包没接上，而是**没在实机上验过**（或走的是原生同步对象 / generic packet 路径）。这条探针可以随时重跑：将来若真有人删了某个包的收发方，它会立刻掉到 1 处。
### 13.3 关于"注册包是否有发送方"探针的失败记录（2026-10-26 第十九轮）

尝试了第二个探针：列出所有 `RegisterPacket<T>`，再找 `new T` / `T {` 作为"发送方"，找不到就报"死包"。结果报了 28 个，**全部是假阳性**——本线的发送方大量通过工厂/字面量构造，而不是 `new`：

- `ProceedPacket.FromValue(...)`、`BtrSpawn.FromValue(...)`、`CommonPacket.SubPacket = X.FromValue(...)` 这类静态工厂；
- `TransitInteractPacket` 之类的 struct 用 `default` + 逐字段赋值后 `SendData(ref packet)`；
- 有些走 `PacketSender.NetworkManager.SendNetReusable(ref …)`，不出现 `new`。

**结论：这个探针不能用**。可靠的仍然是 §13.2 那条——**类型在整棵树里的出现次数是否为 1**（只出现在定义文件里才叫死包）。记录在此，避免下一轮重复踩这个坑。

（顺带得到一条有用的事实：本线的网络包以 `FromValue` 工厂 + 可复用 `CommonPacket` 为主，所以任何"按 `new` 搜索发送方"的静态检查都会失败。）
## 14. 恢复点速查（下个空闲窗口从这里开始，2026-10-26 13:55 写）

当前状态：client `26ECA204…`（W20）已部署，server `571F3C78…`，包 `release/Fika-47965-refactor-20261026-W20-clean2.zip`。本轮已交付 W13–W20 共 8 个增量。

### A. 若用户给了实机日志（最高优先，直接进修复）
| 日志行 | 所在机器 | 判读 → 动作 |
|---|---|---|
| `[TransitDiag] host answer=… reason=… pointState=enabled=…,isActive=…,configured=…` | 主机 | `enabled=no`→追 `HandlerTransitPointState._targetState`，并考虑主机不再用 Enabled 当唯一闸门；`isActive=no`→查 `_playerGroups/_confirmedPlayers/时间窗`；两者都 yes 而仍拒→看 `reason=` 走 access/requirements |
| `[SyncDiag] point-broadcast(-skipped) id=… reason=…` | 主机 | 未出现 → 原生 `set_Enabled` 从未被调用；出现 `skipped` → 看 reason 三个取值 |
| `[SyncDiag] op=… bookkeeping … known=a->b containers=a->b` | 客机 | `b<items` → 仍有别的路径重建 Item；`b==items` → 物品已修 |
| `[SyncDiag] cutscene-immunity-release … role=host viewerHits=… localSwept=…` + `cleared local sniper immunity … wasImmune=` | 主机 | `wasImmune=0` 且主机仍无敌 → 免疫不是从这里释放的，换查 `HandlerSniperZoneImmunity` |
| `[SyncDiag] local-process end … end=native` | 客机 | `end=threw:` → 把异常消息贴出来，按 W11 的 NRE 继续缩 |

### B. 无日志时可做的（按价值排序，均已静态可验）
1. 拆最后一处 `if (true)` 包裹：`ClientTransitController.cs:1343`。
2. `FromValue` 工厂 ↔ `Receive` 分支对称性核对（不要再用"搜 `new`"，见 §13.3）：对每个 `X.FromValue(` 找 `X` 的 `RegisterPacket` 与 `OnXReceived`，缺一侧即断线。
3. §13 矩阵里"零证据"区的接线核对：BTR（`Patches/BTR/*` 9 个补丁）、掉落/尸体（7 个）、空投（6 个）、语音（5 个）、复活（8 个）——只看"发送方与接收方是否成对"，不改逻辑。
4. 结束后回到 R1 等日志，不要为了产出而改联机语义。

### C. 纪律（本轮教训）
- 成员/行数 diff 不能判"功能缺失"（§8.4）；判据是"能力在全树的入口 + 副作用"。
- 静默早退＝诊断盲区（W14 的由来）；失败分支必须带 reason。
- 大文件用 PowerShell 按"出现次数必须为 1"替换，或按行号整块删（结构先行校验），改完立刻编译。
### 13.4 通用子包类型审计（2026-10-26 第二十三轮）

对本线主传输通道的枚举 `EGenericSubPacketType`（19 个成员）逐个统计全树出现次数：**全部 >1，没有"只在枚举里出现"的死成员**。也就是说 generic 通道上不存在"定义了类型却没有收发分支"的断线。

这一条和 §13.2（57 个 packet 里只有 2 个上游同款遗留）合起来，把"协议层是否接线完整"这个问题关掉了：**协议层没有本线特有断线**。剩下能推进的只有实机行为证据。
### 13.7 generic 通道完整性（2026-10-26 第二十六轮）与验收工具

**两个"看起来没实现"的包其实是有意的**
`InventoryPacket` 与 `OperationCallbackPacket` 的 `Execute()` 是**空体**——但这不是缺口：`FikaClient.OnGenericPacketReceived` 先对 `InventoryOperation`/`OperationCallback` 做**显式分发**（`OnInventoryPacketReceived` / `OnOperationCallbackPacketReceived`），其余 17 个类型才走 `packet.Execute()`。19 个类型 = 17 自执行 + 2 显式分发，`Dispose` 之外没有盲区。
→ 由此得到一条判据：**"空 Execute" 不能当"没处理"的证据**，generic 通道有两种投递风格（自执行 / 接收方显式分发），必须看分发处。

**验收工具（新增）**：`tools/fika-evidence.ps1`
把日志里 16 类关键行（过场生命周期、免疫释放/武装、点位广播及跳过原因、主机拒绝答复、库存记账/resync、刷卡、哨兵、跳过投票、未知子包、补丁安装失败、未捕获异常）一次抽出成报告，写到 `.build/fika-evidence-<时间戳>.txt`；全 0 时会打印"这份日志没有 Fika 诊断行"并列出其它候选日志（含 `.build/evidence-*.log` 备份）。运行方式（注意 **UTF-8 BOM + `-ExecutionPolicy Bypass`**，否则 PS 5.1 会按 ANSI 读中文而报语法错）：
`powershell -NoProfile -ExecutionPolicy Bypass -File tools\fika-evidence.ps1 -Log <日志路径>`

**日志现状提醒**：`BepInEx\LogOutput.log` 已被 10-24 10:56 那次约 40 秒的会话覆盖（现仅 0.2 MB、无诊断行）；10-21 那局 43.6 MB 的证据只存在于 `.build/evidence-20261021-2057-LogOutput.log`。
## 15. 零证据区的接线核验（2026-10-26 第三十/三十一轮）

这三条都属于"实机从没验过"，所以先确认它们**是否真的接上**，避免把"没测过"误当成"没做"。

### 15.1 库存失败路径的收尾（无缺口）

`ClientInventoryOperationHandler.FailLocally` 的 finally 里有 `InventoryController?.ReturnHandler(this)`，`ReturnHandler` 会把该 handler 从 `FikaPlayer.OperationCallbacks` 移除。而 `WaitingForCallback` 读的正是这张表，`FikaClientFirearmController`/`FikaClientGrenadeController` 用它挡住扳机与投掷 → **"失败后不能开枪"这条不成立**，无需改动。（W26 只补了记账这一半。）

### 15.2 尸体/布娃娃同步（已接全链路）

| 侧 | 位置 | 行为 |
|---|---|---|
| 主机 | `Components/CorpsePositionSyncer.cs` | `FixedUpdate` 每 4 帧把 `RagdollPackets` 加进 `WorldPacket`；`_isPhysicsDone` 时补一条 `Done=true` 并自毁 |
| 主机 | `FikaHostWorld.cs:69` | `WorldPacket.RagdollPackets` 容器 |
| 客机 | `FikaClient.Callbacks.cs:329-343` | 逐条收进 `FikaClientWorld.RagdollPackets`（带 `MaxPendingRagdollPackets = 256` 上限与 Trim） |
| 客机 | `FikaClientWorld.cs:143-156` | 应用到布娃娃 |
| 死亡瞬间 | `HealthSyncPacket.CorpseSyncPackets` → `ObservedPlayer` | 冲量、手持物、尸体背包描述符 |

`CorpsePositionPacket`（旧类型）在**上游 3.0.4 里同样只出现在定义处**，属遗留死类型，与本次无关。

### 15.3 地面战利品同步（已接全链路）

客机 `ClientGameController.RetrieveLootFromServer` 每 1 秒发一次 `WorldLootPacket`（`Data=[]` 作请求）直到 `HasReceivedLoot`；主机 `FikaServer.Callbacks:646` 收到后回 `WorldLootPacket`；客机 `FikaClient.Callbacks:664` 落到 `LootItems` 再赋值给 `location.Loot`。两侧 `RegisterPacket` 都在（`FikaClient.cs:487` / `FikaServer.cs:497`）。

**结论**：这两条链不是"没接线"，只是**没在实机上验过**。下一步的验收只需一局里分别确认：打死 bot 后客机能看到布娃娃位置与手持物、客机进图能拿到地面战利品列表。
### 15.4 BTR 同步（已接全链路）

| 侧 | 位置 | 行为 |
|---|---|---|
| 主机 | `FikaServer.MapState.cs` 的 `BroadcastBtrSpawn` | 收到 `BtrSpawnOnThePathEvent` → `SendGenericPacket(EGenericSubPacketType.SpawnBTR, BtrSpawn.FromValue(position, rotation, raidId, generation))` |
| 客机 | `Networking/Packets/Generic/SubPackets/BtrSpawn.cs` | `Execute()` → `FikaClient.QueueBtrSpawn(this)`（带 `Generation` 防旧包重放） |
| 交互 | `Main/Patches/BTR/*`（9 个补丁） | `BTRView_GoIn/GoOut`、`BTRSide`、`BTRTurretView_AttachBot`、`BtrController_InitController`、`Player_UpdateBtrTraderServiceData` |

### 15.5 空投 / 飞机 / 可同步对象（已接全链路，走 SyncObjectPackets）

三条补丁把**原生离线逻辑**产出的同步包搬到联机通道上，不是自己造状态：
- `ServerAirDrop_UpdateOfflineClientLogic_Patch`：Postfix 把 `__instance._offlineSyncPacket` 塞进 `HostWorld.WorldPacket.SyncObjectPackets`，落到 `EAirdropFallingStage.Landed` 时 `SetCritical()`。
- `ServerPlane_UpdateOfflineClientLogic_Patch`：同上，`Outdated` 时 `SetCritical()`。
- `SynchronizableObjectLogicProcessor_GetSyncObjectStrategyByType_Patch`：客户端按类型换成 `BaseTripwire` / `ClientAirPlane` / … 的策略，让原生处理器消费这些包。

`AirdropUpdatePacket`（旧类型）只有定义、上游同样只此一处 → 已被 `SyncObjectPackets` + 序列化扩展取代，属遗留死类型。

**§15 小结（零证据区接线表）**：尸体/布娃娃 ✅、地面战利品 ✅、BTR ✅、空投与飞机 ✅、库存失败收尾 ✅ —— 都是"接线完整、实机未验"。剩下唯一没核的是语音（VOIP），它的实现是一整套自建传输（`FikaCommsNetwork`/`FikaVOIPServer`/`FikaVOIPClient`/`FikaVOIPPeer`/`FikaVoiceSession` + 场景租约与失败回退），核对面较大，留到需要时再看。
### 15.6 语音 VOIP（已接全链路：拦在原生语音 API 上）

Fika 不是自己造一套语音，而是在**原生 Dissonance/Mirror 语音 API 上做拦截**，把数据流改道到 Fika 传输：
- `FikaCommsNetwork` 用 `FikaVOIPServer`/`FikaVOIPClient`/`FikaVOIPPeer` 包住原生 `MirrorIgnoranceServer`/`Client`；
- `Main/Patches/VOIP/FikaNativeVoice_Patches.cs`（177 行）给 `MirrorIgnoranceCommsNetwork.CreateClient/CreateServer/Initialize/Update`、`MirrorIgnoranceClient.Connect/Disconnect/SendReliable/SendUnreliable`、`MirrorIgnoranceServer.Connect/Disconnect/AddClient/SendReliable` 挂 Prefix/Postfix，统一以 `FikaCommsNetwork.Find(__instance)` 为闸门；
- 会话生命周期在 `FikaVoiceSession`：场景租约（`SemaphoreSlim`）+ 60 秒启动超时 + 失败回退 `VoipDisabledByInitializationFail()`，raid 结束时 `Shutdown()` 复原 Dissonance 状态；
- `DissonanceComms_Start_Patch`、`Player_IDissonancePlayerType_Patch`、`BasicMicrophoneCapture_UpdateSubscribers_Transpiler` 分别处理启动、玩家类型与麦克风告警抑制。

结论：**接线完整**。语音没有独立的自定义 packet 注册（数据走被拦的 Send* 路径），所以"没有 VoicePacket"不代表没实现。

### 15.7 开关默认值 vs 上游（有没有功能被静默关掉）

全树 `static bool ... = false` 的功能开关，逐个与上游 3.0.4 对照：

| 开关 | 上游 | 本线 | 结论 |
|---|---|---|---|
| `EnableAmbientBotCapFallback` | false | false | 一致 |
| `CollapseBossWavesPerTrigger` | false | false | 一致 |
| `AbortWhenSomeoneDies` | false | false | 一致 |
| `CompactAfterUnload` | false | false | 一致 |
| `EndingRepair` | **true** | false（字段） | 字段值不同，但配置项 `FinalMissionEndingRepair` 默认 true 且在 SetupConfig 绑回，**运行时为 true**；真正的问题是它保护的三段代码被删，W18 已恢复 |
| `FikaTerminalHostility.Enabled`/`ImmunityEnabled`/`HitProbeEnabled` | 同为 false | false | 主动关闭（W3-terminal 之后），非本线私有改动 |
| `KeypadDiag.Enabled` | — | false | 纯诊断 |

**§15 完整结论**：零证据区（尸体/战利品/BTR/空投/语音/库存失败收尾）**全部接线完整**；没有任何功能被本线私有改动静默关闭。这些功能缺的只是实机验证。
### 15.8 哨兵 / 刷卡权限（已接全链路，双向）

| 侧 | 位置 | 行为 |
|---|---|---|
| 客机 | `Main/Patches/World/SentryTerminalAccessPatches.cs`（`Player_ExecuteInteraction_TerminalCard_Patch`） | Postfix 观察原生 `Player.ExecuteInteraction(WorldInteractiveObject, InteractionResult)`；只有"卡检查通过"才算事实（`unlock.Succeed`），并打 `[SentryDiag] swipe:` 或 `swipe-denied:<reader>:<key>`（拒绝也带原因，不是静默） |
| 客机 | `FikaSentryAccess.NoteNativeEmission` → `Broadcast` | 组 `SentryAccessPacket`（`FromClient=1`，客机侧 `HostAccess*` 填 0）→ `SendData(ReliableOrdered)`；无网络管理器时打 `broadcast:none:… -> result=no-network-manager` |
| 主机 | `FikaServer.RaidInteractions.OnSentryAccessPacketReceived` | `FikaSentryAccess.Receive(profileId, raidId, granted, true, hostAccessYes/No/Allies)` —— 主机对自己世界里的哨兵应用事实，并把带判定计数的回声广播给其它端 |
| 客机收 | `FikaClient.Callbacks.OnSentryAccessPacketReceived` | 应用主机给的结论 |

注册：`FikaClient.cs:492` / `FikaServer.cs:524`。所以"客机刷卡主机能看到"这条链**双向齐备**，且每个失败分支都有 `[SentryDiag]` 行（`swipe-denied` / `broadcast:none` / `broadcast:error`），不存在静默早退。
### 15.9 结局 / 撤离（静态链路已完整，W18 恢复后接得上）

`FikaFinalMissionDirector.Tick` 的结局段现在是一条闭合的链：

1. `_localEnding.HasValue && !_localStarted` → `StartLocalEnding()`
2. （W18 恢复）`EndingRepair && _localStarted && !_localComplete` → `WatchLocalEnding()` —— 采样场景/播放进度，丢失原生完成信号时也能推进
3. （W18 恢复）`_localComplete` → `PrepareLocal(game)` —— 设 `ExitStatus`/`ExitLocation`、清并重填 `LocalTriggerZones`、把本机加入 `ExtractedPlayers`、**伤害系数清零**（避免过场里被打死）、停掉任务上报与 DiagnosticPlayerOwner、清 host AI；`if (_localPrepared) return;` 保证幂等
4. （W18 恢复）`EndingRepair && _localComplete && !_exitAuthorized` → `RetryCompletion()`（受 `_nextCompleteRetry + EndingCompleteRetrySeconds` 节流，主机对同一 outcome id 幂等）
5. `_exitAuthorized && _localEnding is { } ending` → `_stopping = true`；客机在线时先发 `ClientExtract` 通用子包；然后 **`game.Stop(ProfileId, ending.ExitStatus, FinallExitZone.FINAL_EXIT_NAME, 0)`** —— 真正结束这一局

也就是说"看完结局能撤离"的静态条件齐了：完成 → 本地预备 → 重试拿到授权 → 调原生 `game.Stop`。**唯一未验的是实机里这条链会不会被原生结局信号正确触发**（W18 之前三段被删，所以历史日志无法证明）。

§15 至此覆盖：尸体/布娃娃、地面战利品、BTR、空投/飞机、语音、哨兵刷卡、库存四类出口、结局/撤离。目标清单里**每一个功能区都有静态落点**；剩下全部是"同一局里跑一遍"。
## 16. Bring Me To Life（RevivalMod）与 Fika 的兼容性审计（2026-10-26 第四十七轮）

**结论：Fika 侧与它完全兼容，且不会挡住它。**

### 16.1 依赖面（REA 读 PE/CLI + ilspycmd 反编译）
`BepInEx\plugins\RevivalMod.dll`（121344 B，sha256 `76e564ca…`，MVID `f7c8f094…`，51 个 TypeDef / 444 个 MethodDef）引用 **`Fika.Core, Version=3.0.4.0`** —— 与本线基线上游同版本。它用到的 Fika API 逐个核过，全部存在且签名一致：

| 它调用的 | 本线状态 |
|---|---|
| `Fika.Core.Modding.Events.FikaNetworkManagerCreatedEvent.Manager` / `DestroyedEvent` | ✅ 定义与签名一致 |
| `FikaEventDispatcher.SubscribeEvent<TEvent>(Action<TEvent>)` | ✅ `where TEvent : FikaEvent`，一致 |
| 上面两个事件的**派发点** | ✅ `NetManagerUtils.cs:335/355`（与上游 342/362 同位置） |
| `IFikaNetworkManager.RegisterPacket<T,TUserData>` / `SendData<T>(ref T, DeliveryMethod, bool)` | ✅ 接口里都有 |
| `FikaSingleton<IFikaNetworkManager>.Instantiated` | ✅ |
| `FikaBackendUtils.ClientType / IsServer / IsClient / IsSinglePlayer` | ✅ |
| `ClientHealthController.ReviveEnabled`（用于判断是否让位） | ✅ |

### 16.2 它的“让位”逻辑（决定了 ④ 的处置）
反编译出的原话（mojibake 已还原）：
> 「检测到 Fika 自带的倒地/复活系统已开启（…fika-server/assets/configs/fika.jsonc → client.reviveConfig.enabled = true）。本模组已主动让位：死亡拦截、濒死状态、自救与爆头即死全部交还给 Fika。要使用 BringMeToLife 的这套功能，请把 client.reviveConfig.enabled 改回 false（两套系统只能二选一）。」

判定实现是 `FikaModule.IsFikaNativeReviveEnabled()` → `MainPlayer.ActiveHealthController.TryCast<ClientHealthController>().ReviveEnabled`。

**实机配置实测**：`SPT_Runtime\user\mods\fika-server\assets\configs\fika.jsonc` 里是
`"enabled": false, "headshotKills": false, "grenadesKills": false, "maxRevives": 0, "bleedoutTime": 60, "reviveTime": 5`
→ Fika 复活本来就是关的 → **模组不会让位**，它应当自己接管。

### 16.3 因此 ④ 的处置
1. Fika 侧保持 `ClientReviveConfig.Enabled = false`（W33），不与该模组抢 `ActiveHealthController.Kill`；两者同时开只会互相打断。
2. Fika 的 `ClientHealthController_Kill_Patch` 在 `ReviveEnabled=false` 时返回 `true`（放行原生 `Kill`），Harmony 前缀链会把后续前缀（模组自己的拦截）继续执行，**不构成阻断**。
3. 若实机仍然救不起来，原因在模组侧或其配置（需要背包里有除颤器 `5c052e6986f7746b207bc3c9`、距倒地者 ≤3 米、长按 5 秒、`criticalStateDuration=180`），或两台机的模组/配置不一致 —— 不再往 Fika 里加第三套复活。
## 17. 客户端 CG 的触发链与两个"别再动"的结论（2026-10-26 第四十八轮）

### 17.1 客户端 CG 到底怎么起（决定了 ③ 的最终处置）

`CutsceneStartSyncPatch`（`Main/Patches/World/RaidInteractionPatches.cs`，Prefix on `CutscenesServerController.StartCutscene`）：

- **主机**：`FilterViewersToSession(__1)` 把座位表收窄到本局真有的人，然后放行原生。
- **客机**：
  1. `FikaLocalCutsceneTrigger.GrantNativeStart(cutsceneId, players)` —— 只有当 2 秒等待期过去、主机仍没派发时，桥才把这次请求"授权"给原生调用，于是客机跑的是**和主机同一套原生链路**（进程、座位表、本地播放）；
  2. 否则 `NoteNativeRequest(cutsceneId)` 记下请求、`__result = null`、`return false` 抑制原生。

`FikaLocalCutsceneTrigger.Tick` 每帧看 `Requests`：超过 `LocalTriggerDelaySeconds`（W30 起 = 2f）且主机没 `NoteHostStart` 过 → `Fire()` 授权自起；`NoteHostStart` 由主机包 `FikaClient.RaidInteractions:63` 记录。

**结论**：
- 主机派发的过场（含转移到达的场景）：客机沿用主机，**不需要**本地自起；
- 主机永远不会派发的、只存在于本机的过场（终点站 / 破冰船 `Icebreaker_1` 这类）：靠 2 秒超时自起 —— 这就是 `UseNativeClientTrigger` 必须保持 **true** 的原因；关掉它等于把这类 CG 永久关掉（与用户"不加 CG 进程就没有终点站/破冰船 CG"的现象完全对应）。
- 之前"加 CG 进程就转不过去"是因为延迟是 **0f**：客机立刻自起，与主机随后派发的到达过场撞同一个 cutscene id。W30 改回 2f 后两半同时成立，**③ 到此关闭，无需再改**。

### 17.2 `FikaTerminalHostility` 保持关闭（不是漏洞，是热修）

`Enabled` / `ImmunityEnabled` / `CensusEnabled` / `HitProbeEnabled` 四个开关当前都是 `false`，文件里带 2026-10-14 的热修备注：

> OFF by default after the first live test: the client took no damage at all and the host process died the moment its own player took fall damage.

即：那半边写的是**代码驱动伤害源**（`SniperFiringZone` / `Minefield` 的 `PlayersWithImmuneToSniperFire`、`GuardedZone` 名单、`BotGroupHostility` 容器），一旦在每台机器上写，就会让玩家对所有代码伤害免疫（含坠落），并在主机上直接把进程带走。

而"刷卡通过 → 哨兵不再敌对组"这条主链由 **`FikaSentryAccess`** 负责（§15.8 已核，双向完整、三个失败面都有日志），**不依赖**这个组件。所以：
- **不要**为了让"终端更自然"去打开它；
- 真要恢复，前提是先把写入范围收窄到"仅权威机、仅该地图原生触发器写的那个容器"，并让伤害观察补丁走启动开关 —— 这是另一件事，不在当前 bug 清单里。
## 18. 战斗层子包全量核对 + 一条被证伪的判据（2026-10-26 第四十九轮）

### 18.1 结论：20 个 common + 30 个 firearm 子包，没有一个"死处理器"

- `Player/Common/SubPackets`（20 个）：除 `FastForwardOutdatedPacket` 用**表达式体** `Execute(...) => player?.HandsController?.FastForwardCurrentState();`（我的空体扫描器一开始把它误报成空体）之外，全部都有实际动作。
- `FirearmController/SubPackets`（30 个）：**零**个空 `Execute()`。

### 18.2 被证伪的判据：不能用"类型名出现次数"判断 firearm 子包是否被发送

我试了一个探针：对 30 个 firearm 子包类型，统计"除自己文件 / 池管理器 / 枚举之外的出现次数"，报出 11 个（`CancelGrenade/CheckAmmo/CheckChamber/CheckFireMode/ExamineWeapon/FirearmLoot/ReloadBoltAction/ToggleBipod/ToggleLauncher/UnderbarrelSightingRangeUp/Down`）。**这 11 个全是假阳性。**

真实机制（`FikaClientFirearmController` + `FikaSerializationExtensions`）：
```csharp
_packet.Type = EFirearmSubPacketType.CheckAmmo;      // 发送侧只写枚举
...
case EFirearmSubPacketType.CheckAmmo: ...            // 序列化按枚举分支写字段
SubPacket = FirearmSubPacketPoolManager.Instance.GetPacket<IPoolSubPacket>(Type);   // 接收侧由池工厂造实例
```
也就是说这些动作**根本不需要** `XxxPacket.FromValue()`，类名自然不会出现在发送侧——它们是有状态的"类型 + 内联字段"协议，而不是"类即协议"。

**判据修订**：在这棵树里，
- 对 `Networking/Packets/*` 顶层包类型，"整树出现次数 == 1" 仍可用于发现死包（§13.2 用过，有效）；
- 对 **common / firearm 子包**，"出现次数 == 1" **无效**（发送走枚举 + 内联字段，接收走池工厂）。要判断是否真被发送，看 `EFirearmSubPacketType.X` / `ECommonSubPacketType.X` 的枚举赋值与 `FikaSerializationExtensions` 的 case 分支是否成对。
## 19. 功能面覆盖表（2026-10-26 第五十一~五十三轮，只读核对）

按"生产者 ↔ 传输 ↔ 消费者 ↔ 生效侧副作用"四处成对核对；凡是四处齐的都标 ✅（= 接线完整，只差实机）。

| 功能面 | 生产 | 传输 | 消费 | 生效 | 结论 |
|---|---|---|---|---|---|
| 手雷 / 信号弹 / 火箭弹 / 取消投掷 | `FikaClientGrenadeController`（9 处枚举赋值）+ `FikaClientQuickGrenadeController` + `FikaClientFirearmController:579-598` | `EFirearmSubPacketType.Grenade/FlareShot/RocketShot/CancelGrenade` + `FikaSerializationExtensions:1283-1293` | 池工厂 4 个入口 | `GrenadePacket/CancelGrenadePacket/FlareShotPacket/RocketShotPacket.Execute` 均有动作 | ✅ |
| 护甲耐久同步 | `FikaPlayer:2031` `ArmorDamagePacket.FromValue` | `ECommonSubPacketType.ArmorDamage` | — | `FikaPlayer:2119 HandleArmorDamagePacket` | ✅ |
| 伤害同步（含破片/地雷） | `ObservedPlayer:599/626/666/759`、`Minefield_Explode_Patch:76` | `ECommonSubPacketType.Damage` | — | `HandleDamagePacket` 链 | ✅ |
| 共享任务进度 | `ClientSharedQuestController`（`FikaPlayer:263` 依 `SharedQuestProgression` 创建） | `Quest/QuestItem/QuestDropItem` 三种包 | `FikaClient.Callbacks:747/763/779`、`FikaServer.Callbacks:684` | 控制器内应用 | ✅ |
| 对话点占用 | `DialogEntryOccupancy.Claim` | `EGenericSubPacketType.DialogEntry`（Claim/Occupancy/Release/FullState） | `FikaServer.RaidInteractions:107` | 客机申请→主机裁定→广播 | ✅ |
| 空投 / 飞机 / 可同步对象 | 三处主机生产者（AirDrop/Plane patch、`FikaHostGameWorld:301/324`） | `WorldPacket.SyncObjectPackets` | `FikaClient.Callbacks:365` → `FikaClientWorld` | `WorldSyncSpawnQueue`（AirPlane 可先入、Tripwire 爆炸移除、Outdated 移除）→ 原生 `ProcessSyncObjectPackets` | ✅ |
| 玩家名牌 / 血条 | `ObservedPlayer:1933` `FikaHealthBar.Create` | 本地渲染（无网络） | — | `ToggleNamePlate`（含光学镜隐藏、缩放、阵营图标、复活中标签） | ✅ |
| 物品寄送（senditem / senditemtoall） | 客户端请求 | `fika/api/senditem`、`fika/api/senditemtoall` | `SendItemController` / `SendItemToAllController` | `MailSendService` 投递 | ✅（服务端侧路由齐备；实机 `allowItemSending: true`） |

**结论**：这一轮把⑤里剩下的可见功能面全部核到"四处成对"，**没有发现新的断线**。加上 §15/§16/§17/§18 的结论，可以说：代码层面已无"缺功能"，剩余全部是"等实机确认"。
## 20. 类型面完整性核对（2026-10-26 第五十九轮）

做法：分别扫描上游 `mods/fika-47965` 与本线 `mods/fika-47965-refactor` 的 `source/Client/Fika.Core`，抽取 `public|internal` 的 `class/struct/interface/enum/record` 类型名，做集合差。

结果：**上游 967 个类型 / 本线 975 个**；「上游有、本线没有」的只有两个，且都不算缺失：

| 类型 | 说明 |
|---|---|
| `LocalSceneSample`（上游 `CutsceneBoundary.cs:2255` 的 internal struct） | 本线同位置改名为 **`SceneSample`**（`internal readonly struct`，`CutsceneBoundary.cs:19`，`SampleSceneOf` 返回它）。纯改名，能力等价 |
| `TransitRecovery`（`Utils/TransitRecovery.cs`） | 主动删除：开关被内联成常量、`MirrorPointEnabled` 改成双向 `SetPointEnabled`（`REBUILD-PLAN` §11 逐块核过，无机制丢失） |

结论：**本线的公开类型面是上游的超集**（多出的 8 个是本线新增：`FikaRallyGate`、`RallyGatePatches` 里的补丁类、`PoseWriteGuards`、`TerminalArmory` 等）。第三方 mod 依赖的类型名一个不缺——这与 §16 对 `RevivalMod` 的逐项核对互相印证。

**剩下能出错的只剩"同名类型里的成员签名/行为"**，那需要实机才能判定，静态层面已无可推进项。
## 21. 本机"依赖 Fika 的插件"全量兼容性核对（2026-10-26 第六十轮）

做法：扫描 `BepInEx\plugins` 下所有 dll 的元数据字符串，找出引用 `Fika.Core` 的插件；再逐个核对它们用到的类型/成员在本线是否存在、事件是否真的被派发。

| 插件 | 它需要的 Fika 面 | 本线状态 |
|---|---|---|
| `RevivalMod.dll` | 见 §16（7 组 API） | ✅ 全在、派发点在 |
| `TransmogHide\TransmogHide.dll` | `Fika.Core.Modding.FikaEventDispatcher`；`Fika.Core.Modding.Events` 的 **`FikaNetworkManagerCreatedEvent` / `FikaNetworkManagerDestroyedEvent` / `FikaRaidStartedEvent`**；`IFikaNetworkManager`；`FikaSingleton<T>`；`LiteNetLib(.Utils)` | ✅ 三个事件都有定义，且派发点齐：网络管理器在 `NetManagerUtils:335/355`，**`FikaRaidStartedEvent` 在 `BaseGameController:643`**（上游 731，同位置语义） |
| `Tyfon.UIFixes.dll` | 只有 `com.fika.core` / `FikaPresent` / `UIFixes.Fika` 这类字符串 | ✅ 反射式软依赖，不硬绑类型，改名也不影响 |

服务端侧：`SPT_Runtime\user\mods` 下引用 `FikaServer`/`FikaShared` 的只有 fika-server 自己的两个 dll —— **没有任何第三方服务端 mod 依赖它的 API**，所以"服务端 dll 固定用 `571F3C78…` 回滚版"不会破坏别的 mod。

**结论**：本机三个 Fika 客户端集成（复活、幻化隐藏、UI 修复）**都不存在"类型或事件缺失"的问题**；它们若出问题，只可能是行为层面（需要实机）。