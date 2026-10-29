# 一页速查（打印/贴副屏用）

## 装什么

`release/Fika-47965-refactor-20261026-W35-immunityscope.zip`（两端同一个包）
client `F7487290…` · server `571F3C78…`

## 只做三件事（约 20 分钟）

### ① 过场 → 看主机还能不能被打死

1. 进图，触发任何一个过场 CG
2. 等 CG 播完，客机能自由走动
3. **立刻**让 bot 打主机（或主机自己踩雷）

- 主机**能**掉血/被打死 = ✅
- 主机还是无敌 = ❌（把主机日志 `cutscene-immunity-release` 整行发我）

### ② 终点站 → 客机按交互

1. 走到任意 transit 点，客机看到提示后按交互
2. 若被拒，记下客机显示的提示文字
3. 主机日志会有一行 `[TransitDiag] entry-requirements …`

- 能转移 = ✅
- 被拒 = ❌（把那行 `entry-requirements` 发我，它会指出是哪条要求没过）

### ③ 物品 → 捡完看是不是"未检视"

1. 捡地面战利品、开箱、捡尸体，连续捡 5–10 件
2. 关背包再打开
3. 检查每件都有名字（不是"未检视"）

- 全部有名字 = ✅
- 有"未检视" = ❌（把客机日志里同时段的 `bookkeeping` 与 `resync-begin` 发我）

## 收尾（两条命令）

两台机各跑：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\fika-evidence.ps1 -Log D:\EFT5.0-47965\BepInEx\LogOutput.log
```

然后（在你这边跑一次，把两份日志路径给我也行）：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\fika-verdict.ps1 `
  -HostLog D:\主机\BepInEx\LogOutput.log `
  -ClientLog D:\客机\BepInEx\LogOutput.log
```

## 发我什么

只要三样，按重要性排：

1. `fika-verdict.ps1` 输出的那张判定表（或两份 `fika-evidence` 报告）
2. 上面 ①②③ 各自的结果（✅/❌ + 现象一句话）
3. 失败那一项的对应日志行（速查里已写明是哪一行）

## 若时间只够做一件

做 **①**。它对应"主机无敌"这个最影响体验的 bug，而且日志判据最硬（`wasImmune=1` 或 `wasImmune=0`）。
