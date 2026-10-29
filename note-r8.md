
| 2026-10-18 | round 8 / W3-transit `Fika-47965-refactor-20261018-W3-transit.zip` | 转移控制器启发式清理：删除 `NudgeStalledPresses`（主机替客机补按）、`RecheckCountdownParticipants`+`PauseCountdown`+`TryResumeCountdown`（离开点暂停/恢复看门狗）共 4 个方法 + 3 处调用，文件 166.7KB→156.0KB；`Transit: Abort When Someone Leaves` 相应变为无效开关；三项目 0 error |
