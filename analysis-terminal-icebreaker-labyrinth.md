# 原版一致性排查（2026-10-19）

对象：`mods/fika-47965-refactor`（最新包 `W4-fps`）。以下每条都带 file:line 证据，区分"已证实"与"待实现"。

## 1. 终点站（码头）开局 CG 只有字幕、没有画面，后续 CG 正常

**已证实的事实**
- 开局 CG 由 `FikaFinalMissionDirector.DriveStoryIntro()` 驱动：只在主机执行，条件是 `CutscenesServerController._timelinesMapBank != null` + `StartCutsceneByStartRaid` 标记 + `_frames >= IntroDelayFrames`（`FikaFinalMissionDirector.cs:386-429`），随后走 `StartCutscene(cutscene, PlayerIds(players), false, false, 0)`。
- 客机侧的原生播放入口是 `RaidInteractionSync.PlayLocalNativeProcess(process)`（`RaidInteractionSync.cs:766`），由补丁 `RaidInteractionPatches.cs:104` 在 `CutsceneProcess` 产生时调用。
- 我在重置里把 `CutsceneBoundary` 从 2813 行改成了 150 行，**删掉了原来的"本地播放泵"**（每帧推进 `CutsceneProcess.afterStartTimeElapsed`、并在进程建立后做一次接管）。

**结论（根因判断）**
后续 CG 有画面 ⇒ 播放链路本身可用；只有**开局这一条**没画面 ⇒ 它发生在世界刚建立、`PlayLocalNativeProcess` 被调用的那一刻，原先由播放泵补的"首帧接管 / 时间推进"没了，客户端进程存在但相机没被切过去，于是只剩字幕。

**修复方案（下一轮实现）**
在 `CutsceneBoundary` 里恢复**最小播放泵**：仅当存在本地 `CutsceneProcess` 且其 `afterStartTimeElapsed` 在 `PlaybackIntervalSeconds` 内没有推进时，调用原生 `OnUpdate(dt)` 推进一次；进程建立后的首帧做一次 `CutscenesClientController` 的相机接管。这也解释了为什么后续 CG 正常（它们由玩家交互触发，时间点在世界稳定之后）。

## 2. 终点站开局"装备被没收并转移到军械库"缺失

**已证实的事实**
- 全树搜索 `Confiscat` / `Armory` = **0 命中**；`Terminal` 250 处命中全部是地图/本地化引用。
- ⇒ Fika 这条线**完全没有实现**该机制，原版行为在联机下从未发生。

**结论**：这是**缺失功能**，不是坏掉的补丁。

**修复方案（下一轮实现）**
在主机的 `CoopGame.InitPlayer`（Terminal 地图）里，对每个玩家执行一次：把装备槽与背包内容物转移到 Terminal 的军械库 stash（`FikaGlobals` 现有的 stash 命名规则），然后清空 `Equipment`/背包。需要先用 SPT 数据确认军械库容器 id（Terminal 的 `Location` 里有专用 stash 定义），再按玩家逐个执行并同步给客机（客机侧由 `ReplaceInventory` 应用）。

## 3. 破冰船：轮机室 CG 之后客机变无敌

**已证实的事实**
- `Main/Patches/World/TerminalHostilityPatches.cs` 整个文件是**禁用状态**（`Disabled = true`，36 行），注释写明原因：上一次联机测试里"客机完全不掉血、主机一受坠落伤害就崩"，于是把 7 个补丁（`Player.ApplyDamageInfo`、`SniperFiringZone.IsPlayerCanBeDamaged`、`BorderZone.ProcessIncomingPacket`、两个 immunity 写入点、`SniperFireTargetManager`、`BotGroupHostility.AddEnemyAction`）全部撤掉，并说明 `FikaTerminalHostility.ImmunityEnabled` 默认 false。
- ⇒ 当前构建**没有任何**免疫写入补丁在跑，所以客机无敌不是这些补丁造成的，而是**原生 CG 赋予的免疫没有被解除**：轮机室 CG 结束时原版会清掉狙击区/边界区的免疫，而客机的"进程结束"信号来自主机，丢失或时序不对就会永久免疫。

**修复方案（下一轮实现）**
在客机的 `CutsceneProcess` 结束时（`RaidInteractionSync.EndCutscene` / `OnViewComplete` 路径）显式调用原生的免疫解除（`SniperFiringZone` / `BorderZone` 对应的 `SetPlayerImmunity(false)` 一类接口，需先用 il2cpp 反射确认方法名），并加一条仅在"本机玩家仍免疫 30 秒后"触发的兜底。

## 4. 迷宫：五个出生密室，队友应分到不同密室

**已证实的事实**
- `ClientGameController.CheckSpawnTogether()`（`:348-357`）：迷宫 + `RandomLabyrinthSpawns` 时返回 `false`，否则看 `PlayersSpawnPlace == SamePlace`。
- 客机自选点：`CreateSpawnSystem(profile)` 里 `_spawnPoint = SpawnSystem.SelectSpawnPoint(ESpawnCategory.Player, profile.Info.Side, null, null, null, null, profile.Id)`（`:366`），随后 `ClientSpawnPosition/Rotation` 取自该点。
- ⇒ 每台机器各自用同一份 `SpawnPointParams` 与同一时间基选点，**没有任何去重**，所以两人会落到同一个密室。

**修复方案（下一轮实现）**
主机在开局时按玩家顺序分配互不相同的密室索引（5 个点，按 `ProfileId` 稳定排序取模去重），随 `SpawnPositionPacket` 一起下发；客机在 `CheckSpawnTogether()` 为 false 时改用主机指派的位置而不是自选。这样既保留"随机开始密室"的原版意图，又保证队友分开。

---

## 实施顺序建议
1. 第 3 项（无敌）——影响最大、改动最小（一个结束路径 + 一条兜底）。
2. 第 1 项（开局 CG 无画面）——恢复最小播放泵。
3. 第 4 项（迷宫分房）——主机分配 + 客机采用。
4. 第 2 项（装备没收）——唯一需要新写功能的一项，工作量最大。
