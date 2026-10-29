
| 2026-10-21 | W9-revert `Fika-47965-refactor-20261021-W9-revert.zip` | 按实测回退 W9：单座表让客机**真的起了一份本地 CG 进程并长时间处于"过场中"**，而该进程在原生链路上不会结束（没有结束事件），于是客机的转移交互一直被判不可用 → “无法多人转移终点站”。用户实测 `W8-revert` 可转移、W9 之后不可，故 `FikaLocalCutsceneTrigger.Fire` 恢复为 W8-revert 的形态（不再提前用单座表启动）。包内容等价于 W8-revert |
