
| 2026-10-20 | W7-cfg-trace `Fika-47965-refactor-20261020-W7-cfg-trace.zip` | 配置初始化仍在 `SetupConfig()` 抛 NRE（IL2CPP 无行号，且 `RetireRemovedEntries`/`ColorConfigConverter.Register` 两道守卫都没有报错 ⇒ 抛点在主绑定体内）。本轮在 `SetupSetting` 里加**绑定序号 + 段落/键名日志**（`[Config] bind N: section/key`）：下次启动日志里最后一条 `[Config] bind` 就是崩溃点之前的最后一个条目，可精确定位 |
