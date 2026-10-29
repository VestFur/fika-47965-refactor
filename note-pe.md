
| 2026-10-19 | W6-transit-pollevent \`Fika-47965-refactor-20261019-W6-transit-pollevent.zip\` | 去掉性能开销最大的轮询：\`TransitPresenceReporter.Poll\` 里每 0.25 s 对**所有点位体积**做几何扫描（\`FindPointInside\`）改为条件触发——新增 \`GeometricFallback = false\`，默认不再扫描；在场与提示改由**原生触发器的 Enter/Exit 补丁** + 主机 \`Show/Confirm/InactivePoint\` 事件驱动。同一 tick 里只剩主机倒计时时钟（本身需要节拍） |
