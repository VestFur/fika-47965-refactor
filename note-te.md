
| 2026-10-19 | W6-transit-exit `Fika-47965-refactor-20261019-W6-transit-exit.zip` | 客机离开转移点仍显示"互动"：`OnClientPlayerExit` 之前只上报了"离开点位"，没清本机交互状态；现在同时 `InteractPacket = null` 并调用 `FikaPlayer.UpdateInteractionCast()`（原生清除交互投射/提示的那一步），离开即消失。另说明：按下后显示"无法使用转移功能"是主机严格门的 `NonAllTeammates` 答复（对方尚未按下），不是失败——双方都按下后正常开始倒计时 |
