
| 2026-10-19 | 打包口径 | **不再打包 `Fika.Headless.dll`**：包里只含 `Fika.Core.dll` + `FikaServer.dll`；用户本机最新 Headless 另存副本到 `release/headless/`，部署时用用户已装的那份 |
| 2026-10-19 | 加载卡在"物资池"防护 | `HostGameController.InitializeLoot` 里对 `TerminalArmory.Inject` 调用**整体加 try/catch**：此前若 `FikaSingleton<IFikaNetworkManager>.Instance` 在那一刻不可用，异常会从 `InitializeLoot` 抛出，整局卡在"正在加载物资池"。现在失败只记 `LogError`，战利品照常生成 |
