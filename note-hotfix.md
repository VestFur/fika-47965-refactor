
| 2026-10-19 | hotfix / W4-fps `Fika-47965-refactor-20261019-W4-fps.zip` | **进局掉帧修复**：`CutsceneBoundary.Tick` 原先在"两个控制器还没有 timeline bank"时**每帧**执行 `FindObjectOfType<MapTimelinesBank>()`（整场景扫描）；在没有该对象的图上会永久每帧扫描，正是重置包掉帧的来源。改为**最多 6 次、间隔递增（1..6 秒）**的有界尝试，找到一次即缓存；三项目 0 error |
