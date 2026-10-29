# fika-47965-refactor

Fika 联机 mod 的重构线，目标环境：**SPT 5.0 / EFT 1.2.0.47965**（Fika 基线 3.0.4）。

私有仓库，仅自用与备份；**不是**官方 Fika 发布渠道。

## 目录

| 路径 | 内容 |
|---|---|
| `source/Client/Fika.Core/` | 客户端插件（BepInEx 6 / Harmony / LiteNetLib） |
| `source/Headless/Fika.Headless/` | 无头宿主插件（专用服务器） |
| `source/Server/FikaServer/` 与 `FikaShared` | SPT 服务端 mod（大厅、匹配、配置下发、物品寄送） |
| `release/` | 最新可部署包（**按组件分包**，见下） |
| `analysis/` | 计划、对账结论、静态审计报告 |
| `REFACTOR-TRACKER.md` | 变更台账（唯一权威：每处改动的编号/文件/理由/状态） |
| `TARGETS.md` | 目标清单（T1–T10 + 追加区 N1–N4，含每项判据与"未解决就删补丁"规则） |
| `HANDOFF-*.md` | 交接说明 |

## 发布包（`release/`）

| 包 | 用途 |
|---|---|
| `*W47b-client-server*.zip` | 客户端 + 服务端：`BepInEx/plugins/fika/Fika.Core.dll` + `SPT_Runtime/user/mods/fika-server/`（含 `assets/` 配置、语言库、无头脚本） |
| `*W47-headless*.zip` | 仅 `Fika.Headless.dll`，给专用宿主用 |

两个包都是**直接解压覆盖到游戏根目录**，不需要安装器。

> ⚠️ **源码比包新**：仓库里的代码已包含 W48/W49（转移点误报、CG 后无敌、主机等观众确认黑屏）的修复，但这两个版本**尚未打包**。要用最新代码请自行构建（见下）。

## 构建

```powershell
# 需要 -p:GameDir 指向你的游戏目录；Server 也必须带，否则报 SPT 5.0 runtime missing
dotnet build source\Client\Fika.Core\Fika.Core.csproj        -c Release -p:GameDir=<游戏目录> --nologo
dotnet build source\Headless\Fika.Headless\Fika.Headless.csproj -c Release -p:GameDir=<游戏目录> --nologo
dotnet build source\Server\FikaServer\FikaServer.csproj     -c Release -p:GameDir=<游戏目录> --nologo
```

目标框架：客户端 / Headless / 服务端均为 **net10.0**（本仓库按 .NET 10 运行时验证）。

## 当前状态（2026-10-27）

- 三项目构建 0 error
- 已实机确认：客机可随主机转移「终点站」
- 待验证：转移点互动提示、过场后伤害恢复、码头落地黑屏、无头低开销
- 未解决：结局界面（W18 被判无效）、客机不播落地 CG

详细进度见 `TARGETS.md` 与 `REFACTOR-TRACKER.md`。
