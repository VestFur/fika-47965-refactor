# Fika 47965 重构线（W1a）

- 包内顶层即 `BepInEx/` 与 `SPT_Runtime/`，解压到游戏根目录即可部署。
- `REFACTOR-TRACKER.md` 是**唯一权威变更表**：所有改动（含子代理）动代码前先读、动完必须更新。
- 本波（W1a）改动：删除不参与构建的 `source/tests`、`source/docs`、`source/scripts`；`LoadSync.Report` 置空（保留 `Warn`）。
- 构建：Client/Headless/Server 三项目 0 error。
