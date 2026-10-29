
| 2026-10-19 | W6-transit-ready `Fika-47965-refactor-20261019-W6-transit-ready.zip` | 转移流程按原版语义重排：①客机按下 = **就绪**，`EveryoneInteracted` 不再回 `NonAllTeammates`（只记录、不提示），客机不再看到"无法使用转移功能"；②倒计时改由**主机自己的按下**启动：`InteractWithTransit` 里非本机玩家（客机）注册后直接 return，主机按下且全员就绪才 `BeginCountdown`；headless 主机没有本机玩家，故其门一满足即开始。客机离开点位时清本地 `InteractPacket` 只影响本地提示缓存，主机侧的就绪记录仍以交互包为准，离开时由在场包撤销 |
