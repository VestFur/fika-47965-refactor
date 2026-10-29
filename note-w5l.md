
| 2026-10-19 | W5-labyrinth `Fika-47965-refactor-20261019-W5-labyrinth.zip` | 迷宫出生默认分开：`ClientGameController.CheckSpawnTogether()` 对 `labyrinth` 恒返回 false（直接进图和转移进图一致），只有关掉 `Random Labyrinth Spawns` **且**战局设置为"生成在一起"时才同点；其它地图行为不变；三项目 0 error |
