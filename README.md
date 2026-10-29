# fika-47965-refactor

Fika 联机 mod 的重构源码，目标环境 **SPT 5.0 / EFT 1.2.0.47965**（Fika 基线 3.0.4）。

> 本仓库**只放源码**。可部署版本见 **Releases** 页面（组件分包，直接解压覆盖到游戏根目录）。

## 结构

| 路径 | 内容 |
|---|---|
| `source/Client/Fika.Core/` | 客户端插件（BepInEx 6 / Harmony / LiteNetLib） |
| `source/Headless/Fika.Headless/` | 无头宿主插件（专用服务器） |
| `source/Server/FikaServer/`、`FikaShared` | SPT 服务端 mod（大厅、匹配、配置下发、物品寄送） |

## 构建

```powershell
dotnet build source\Client\Fika.Core\Fika.Core.csproj            -c Release -p:GameDir=<游戏目录> --nologo
dotnet build source\Headless\Fika.Headless\Fika.Headless.csproj  -c Release -p:GameDir=<游戏目录> --nologo
dotnet build source\Server\FikaServer\FikaServer.csproj          -c Release -p:GameDir=<游戏目录> --nologo
```

三个项目均为 **net10.0**；`-p:GameDir` 必填（Server 缺了会报 `SPT 5.0 runtime missing`）。

## 版本

可部署 zip 见 Releases：`client-server`（含服务端 `assets/` 数据）与 `headless`（仅 `Fika.Headless.dll`）分开打包。
