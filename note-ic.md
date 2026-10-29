
| 2026-10-19 | W6-immunity-clear `Fika-47965-refactor-20261019-W6-immunity-clear.zip` | "无伤害的攻击特效/音效"：终端/破冰船的狙击-边界区特效在客机播放，但伤害被本机仍持有的原生免疫条目拒绝。新增 `ReleaseLocalSniperImmunity`：每局首次进入 tick 时把**本机玩家**从所有 `PlayersWithImmuneToSniperFire` 容器里移除一次（`RemoveImmunePlayer(raidId)`），`DropRaidState` 复位；保留过场结束时的参与者清理 |
