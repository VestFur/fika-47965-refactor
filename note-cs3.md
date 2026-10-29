
| 2026-10-19 | W5-cutscene-sync-3 `Fika-47965-refactor-20261019-W5-cutscene-sync-3.zip` | 定位到真因：客机的"本地触发器兜底"（`FikaLocalCutsceneTrigger`）在主机已同步的情况下**又起了一份自己的进程**，观众表用的是地图原生 8 席 → 客机显示 1/8、永不完结、时间轴上的门动作也只在本机跑。修复：`LocalStartsEnabled = false`（`NoteNativeRequest`/`GrantNativeStart`/`Tick` 全部早退），客机不再自建进程，只播主机 `CutsceneStartPacket` 下发的那一份；门等时间轴动作随主机进程一起同步 |
