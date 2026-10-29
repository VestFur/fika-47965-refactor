
| 2026-10-19 | W5-terminal-gear-5 `Fika-47965-refactor-20261019-W5-terminal-gear-5.zip` | 军械库注入改为**只在码头战局触发**：判断依据从菜单的 `RaidSettings.SelectedLocation.Id` 换成	extbf{本次战局自身的} `InitializeLoot(Location location)` 的 `location.Id`（转移进码头时菜单仍停留在上一张图，用菜单值会误判）；非码头地图完全跳过注入；包里不含 Headless |
