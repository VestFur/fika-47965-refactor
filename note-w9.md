
| 2026-10-21 | W9-cutscene-seat `Fika-47965-refactor-20261021-W9-cutscene-seat.zip` | 破冰船客机 CG：实测客机计数器出现 **1/4、2/4**（主机 1/2），说明客机本地自建的 process 座位表来自整个世界且**授权未命中导致没有收窄**，进程永远等不齐 → 不播、后续无敌也无人解。修复：`FikaLocalCutsceneTrigger.Fire` **直接用只含本机 playerId 的单座表**启动本地进程（不再依赖 grant 收窄），成功即记 `[CutsceneDiag] local process started cutscene=… process=… viewers=1` 并返回。构建 0 error |
