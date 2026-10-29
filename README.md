# fika-47965-refactor

Fika 联机 mod 的重构源码，目标环境 **SPT 5.0 / EFT 1.2.0.47965**（Fika 基线 3.0.4）。

> **License**: [CC BY-NC-SA 4.0](LICENSE) — 非商业用途；修改与再分发须沿用同一协议并署名原作者。
> 本仓库是 [Project Fika](https://github.com/project-fika/Fika-Plugin) 的**修改版（派生作品）**，改动见 [MODIFICATIONS.md](MODIFICATIONS.md)、署名见 [NOTICE](NOTICE)。
> 不含任何 Battlestate Games 的代码、资产或游戏文件。

可部署版本见 **Releases**（按组件分包，解压覆盖到游戏根目录即可）。

## 结构

| 路径 | 内容 |
|---|---|
| `source/Client/Fika.Core/` | 客户端插件（BepInEx 6 / Harmony / LiteNetLib） |
| `source/Headless/Fika.Headless/` | 无头宿主插件（专用服务器） |
| `source/Server/FikaServer/`、`source/Server/FikaShared/` | SPT 服务端 mod（大厅、匹配、配置下发、物品寄送） |
| `source/*/Licenses/` | 随源码分发的第三方许可（LiteNetLib、Open.NAT、Mirror、K4os LZ4 = MIT；SIT = NCSA） |

## 构建

需要 .NET 10 SDK，并准备一份已安装 SPT 5.0 的游戏目录（下面写作 `<GameDir>`）。三个项目都通过 `GameDir` 从游戏安装里解析 BepInEx 与 SPT 程序集，仓库本身不分发这些二进制。

```powershell
dotnet build source\Client\Fika.Core\Fika.Core.csproj            -c Release -p:GameDir=<GameDir> --nologo
dotnet build source\Headless\Fika.Headless\Fika.Headless.csproj  -c Release -p:GameDir=<GameDir> --nologo
dotnet build source\Server\FikaServer\FikaServer.csproj          -c Release -p:GameDir=<GameDir> --nologo
```

- `-p:GameDir` 必填；Server 缺了会报 `SPT 5.0 runtime missing`
- 客户端构建前需确认 `<GameDir>\BepInEx\interop\Assembly-CSharp.dll` 存在（SPT 5.0 的 interop 程序集）
- 若 MSBuild 报临时目录拒绝访问，先把 `TEMP`/`TMP` 指到可写目录

## 版本

| Release | 说明 |
|---|---|
| `v2026.10.29` | 最新：转移点提示修复、过场后伤害恢复（PR #1）、过场卡死兜底 |
| `v2026.10.27-W47b` | 首个按组件分包的版本 |

`client-server.zip`：`Fika.Core.dll` + `SPT_Runtime/user/mods/fika-server/`（含 `assets/` 配置与语言库）
`headless.zip`：仅 `Fika.Headless.dll`
