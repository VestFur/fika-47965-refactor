
| 2026-10-18 | W5-cutscene-sync-2 `Fika-47965-refactor-20261018-W5-cutscene-sync-2.zip` | 按反馈收紧：①去掉**周期扫描**——原生进程广播改由既有的 `CutsceneStartSyncPatch.Postfix`（每次原生 `StartCutscene` 必过）承担，`TickPlayback` 只在开局**扫一次**补历史进程（`_startupDispatched`，每局 `Reset` 复位）；②"等待玩家中"计数对齐真实玩家：主机在 `CutsceneStartSyncPatch.Prefix` 里调用新 `FilterViewersToSession`，把原生观众表裁到会话内玩家（2 人局显示 2/2，不再等机器人/外队席位） |
