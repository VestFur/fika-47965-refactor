
| 2026-10-19 | W5-immunity `Fika-47965-refactor-20261019-W5-immunity.zip` | 破冰船轮机室 CG 后客机无敌：在 `RaidInteractionSync.EndCutscene` 结束流程里、广播之前，对 `CutsceneStartPacket.NetIds` 的每个参与者（`ToRaidId` 映射）调用本世界所有 `PlayersWithImmuneToSniperFire.RemoveImmunePlayer(raidId)`。原版在 process 结束时清狙击免疫；Fika 客机的 process 由主机结束，之前没人清，于是永久免疫。三项目 0 error |
