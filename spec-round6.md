

## 附：重写规格 —— FikaFinalMissionDirector（round 6 产出）

当前文件 1091 行、9 处修复标记。对外 22 个成员，调用方与最小语义如下；实现时**只保留这些成员**。

| 成员 | 调用方 | 最小语义 |
|---|---|---|
| Reset() | RaidInteractionSync.cs:277 | 每局开始清空状态 |
| Tick() | CoopHandler.cs:309 | 每帧：推进计时器/开场、处理收包队列 |
| TickTimer(StartCutsceneByTimer) | FinalMissionPatches.cs:37 | 计时到点且 bank 存在时触发一次剧本 |
| SendSnapshot(peer) | RaidInteractionSync.cs:712 | 主机：把结局状态发给某 peer，返回是否已处理 |
| DeferStop (get) | CoopHandler.cs:345 / CoopGame.cs:1791 / FikaTimeManager.cs:59 | 结局进行中为 true，阻止提前结束战局 |
| Bound (get) | FinalMissionPatches.cs:36/52 | 本局是否存在 Fika 接管的结局 |
| Owns / Arm / DrivesZone(FinallExitZone) | FinalMissionPatches.cs:16/17/27 | 接管判定、接管并拦截、区域驱动判定 |
| IsLocalFinalExitAction / LocalExitAction / SuppressExitAction / LocalTimelineComplete | FinalMissionPatches.cs:52-56 | 本机执行/抑制最终撤离动作 |
| HandleCompletion / Receive / ReceiveQuestStates | 网络收包路径 | 应用结局完成与状态快照 |
| PlayerExtracted / HasLocalEnding / FinalExitZoneText / EndLocalOnTimeout / EndingRepair | 各调用点 | 状态查询与收尾 |

**实现顺序**：骨架（字段 + 上述成员简单实现）→ 编译收敛 → 补 Tick/SendSnapshot/Receive 真实逻辑 → 每步一个包。

| 2026-10-18 | round 6（无包，树 == W3-sweep2） | 抽取 `FikaFinalMissionDirector` 对外 API 面（22 成员 / 6 个调用方），写入本节重写规格；不改代码，构建保持 0 error |
