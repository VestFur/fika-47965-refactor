# 本批改动验收清单（W28–W33，2026-10-26）

> 一批 4 个修复，跑一局就能全部验到。装 **`release/Fika-47965-refactor-20261026-W35-immunityscope.zip`**（两端同一个包），
> client dll 应为 `E659F86D…`、server `571F3C78…`。
> 收尾仍用 `tools\fika-evidence.ps1` + `tools\fika-verdict.ps1`。

---

## ① BTR 客机看得见车（W28）

**动作**：等 BTR 沿路线出现（或让主机叫 BTR），客机站在路边看。

| 结果 | 说明 |
|---|---|
| ✅ 客机**看得见**车身、车轮在转、能上车 | 修复生效 |
| ❌ 仍只有引擎声、看不见车 | 贴客机日志里 `[BtrDiag] view shown by Fika` 那一行（**有没有这行**本身就是判据：没有＝`EnsureBtrViewVisible` 没被调用到，有但仍看不见＝`ShowView()` 不足，需要进一步看 `_viewVisible` 与网格开关） |

---

## ② 客机**先按**也能撤离 / 转移（W29 + W32）

### ②-a 黑车（付费撤离）
**动作**：客机**先**站进黑车撤离点按交互，主机随后按。

| 结果 | 判据 |
|---|---|
| ✅ 两人一起撤出 | — |
| ❌ 主机走了客机还在 | 客机日志出现 `[CarExtract] kept the earlier local activation: point=… local=… host=…` 就是 W32 生效了（若仍失败，说明还有第三个闸门，把这行连同 `[CarExtract] activation received` 一起发我） |

### ②-b 终点站转移
**动作**：客机**先**按 transit 点，主机随后按。

| 结果 | 判据 |
|---|---|
| ✅ 两人一起转到下一张图 | — |
| ❌ 客机没转 | 主机日志里找 `[TransitDiag] grace re-registered profile=… point=…`（W29 补注册生效）或 `[TransitDiag] start-gate point=… ready=… missing=…`（谁没到位） |

---

## ③ 加着 CG 进程也能转移；CG 照样播（W30）

**动作**：保持默认（不要动 CG 相关开关），从**海岸线**转到**终点站**。

| 结果 | 判据 |
|---|---|
| ✅ 转移成功，且到达后终点站 CG 播了 | W30 两半同时成立 |
| ❌ 转移失败 | 多半是客机仍自起了本地 CG：看客机日志有没有 `[SyncDiag] local-process start pid=…` **紧挨着**转移 |
| ❌ 转移成功但**没有** CG | 说明主机侧没派发、客机 2 秒后也没自起：把客机日志 `local-process` 与 `start-gate` 两段发我 |

---

## ④ 救人（W33：交给 Bring Me To Life）

**前提**：本机已装 Bring Me To Life（`BepInEx\plugins\RevivalMod.dll` + `RevivalModServer`），Fika 自带复活**保持关闭**（`fika.jsonc` 里 `reviveConfig.enabled: false`），两套只能二选一。

**动作**：客机被打倒（不要爆头，配置里爆头仍即死），主机走过去救。

| 结果 | 判据 |
|---|---|
| ✅ 出现救援提示、长按 5 秒救起 | 模组工作正常 |
| ❌ 客机直接死、没有倒地 | 说明模组没接管：确认**两端**都装了 `RevivalMod.dll`，且 `fika.jsonc` 的 `reviveConfig.enabled` 仍为 `false` |
| ❌ 有倒地但队友救不了 | 检查：救人者背包要有**除颤器**（`5c052e6986f7746b207bc3c9`）、距离 **≤3 米**、长按 **5 秒**、倒地后 **180 秒**内 |

> 若你更想用 Fika 自带的复活：把 `fika.jsonc` 的 `reviveConfig.enabled` 改成 `true`，并停用 RevivalMod（两者只能二选一）。

---

## 发我什么

1. 上面 ①②③④ 各自 ✅/❌（一句现象即可，❌ 的按"判据"列贴对应那一行）
2. 两台机的 `.build\fika-evidence-*.txt`
3. 有 ❌ 时，对应的 `fika-verdict` 判定表

## 已知不在本批范围

- `FikaTerminalHostility`（终端/哨兵之外的代码伤害源写入）—— 2026-10-14 热修关闭，见 `REBUILD-PLAN` §17.2，**不要打开**
- 诊断行清理 —— 等这批验收通过后再做
