
| 2026-10-20 | W7-cfg-start `Fika-47965-refactor-20261020-W7-cfg-start.zip` | 启动失败定位：日志 `Fika initialization failed; cooperative play is unavailable` + `NullReferenceException at FikaConfig.SetupConfig()`（无行号，IL2CPP）。`SetupSetting` 内部本就有 try/catch，故抛点在它之外；本轮把 `SetupConfig` 开头的两个前置步骤（`RetireRemovedEntries()`、`ColorConfigConverter.Register()`）改为**best-effort + 具名日志**，任一抛异常都不再拖垮联机初始化，并在日志里指明是哪一步 |
