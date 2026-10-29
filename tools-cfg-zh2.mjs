import fs from 'node:fs';
const p = 'mods/fika-47965-refactor/source/Client/Fika.Core/FikaConfig.cs';
let s = fs.readFileSync(p,'utf8').replace(/\r\n/g,'\n');
const map = {
'Allow VOIP':'允许语音','Auto Extract':'自动撤离','Auto Use Headless':'自动使用无头主机','AZERTY Mode':'AZERTY 键位','Chat Key':'聊天按键',
'Connection 超时':'连接超时','Decrease Opacity In Peripheral':'周边视野降低不透明度','Developer Mode':'开发者模式','Download Profile Button':'下载档案按钮',
'Drone Mode':'无人机模式','Easy Kill Conditions':'简化击杀条件','Enable Chat':'启用聊天','Enable IPv6':'启用 IPv6','Extract Key':'撤离按键',
'Final Mission: Ending Repair':'终局任务：结局修复','Flare Extract: Feedback 秒':'信号弹撤离：反馈秒数','Flare Extract: Replicate Exit Visual':'信号弹撤离：复制撤离视觉',
'Flare Extract: Squad Shares Activation':'信号弹撤离：小队共享激活','Force Bind IP':'强制绑定 IP','Force IP':'强制 IP','Free Camera Button':'自由视角按键',
'Full Health Color':'满血颜色','Give Up Key':'放弃按键','Hide Health Bar':'隐藏血条','Hide Name Plate in Optic':'开镜时隐藏名牌',
'Inert Outside Fika Raids':'非 Fika 战局中不生效','Keybind Overlay':'按键提示层','Low Health Color':'低血颜色','Max Bundle Lock':'最大打包锁定',
'Max Distance to Show':'显示的最大距离','Minimum Opacity':'最小不透明度','Name Plate Scale':'名牌缩放','Name Plate Text Color':'名牌文字颜色',
'Name Plates Use Optic Zoom':'名牌使用开镜缩放','Net Maximum Events Per Frame':'每帧最大网络事件数','No Loot':'不生成战利品',
'Opacity in ADS':'开镜时不透明度','Ping Button':'标记按键','Ping Color':'标记颜色','Ping Minimum Opacity':'标记最小不透明度',
'Ping Scale With Distance':'标记随距离缩放','Ping Size':'标记大小','Ping Sound':'标记音效','Ping System':'标记系统','Ping Time':'标记持续时间',
'Ping Use Optic Zoom':'标记使用开镜缩放','Play Ping Animation':'播放标记动画','Player Load Priority':'玩家加载优先级',
'Prefer IPv6':'优先 IPv6','Quest Types':'任务类型','Send Rate':'发送频率','Shared Boss Experience':'共享首领经验',
'Shared Kill Experience':'共享击杀经验','Show Broken Limbs':'显示骨折','Show Effects':'显示特效','Show Extract Message':'显示撤离提示',
'Show Feed':'显示击杀信息','Show HP% instead of bar':'显示血量百分比而非血条','Show In-Game Player List':'显示局内玩家列表',
'Show Notifications':'显示通知','Show Official Version':'显示正式版本','Show Ping During Optics':'开镜时显示标记','Show Ping Range':'显示标记距离',
'Show Player Faction Icon':'显示玩家阵营图标','Show Player Name Plates':'显示玩家名牌','Simulated Latency (ms)':'模拟延迟（毫秒）',
'Simulated Packet Loss (%)':'模拟丢包（%）','Transit: Client Press Fallback':'转移：客机按下兜底','UDP Port':'UDP 端口',
'Use Fika NAT Punch Server':'使用 Fika NAT 穿透服务器','Use NAT Punching':'使用 NAT 穿透','Use Occlusion':'使用遮挡剔除',
'Use Port Mapping':'使用端口映射','Write 在线玩家计数':'写入在线玩家计数'
};
let n = 0;
for (const [en, zh] of Object.entries(map)) { const before = s; s = s.split('"' + en + '"').join('"' + zh + '"'); if (s !== before) n++; }
// two labelled leftovers
s = s.split('倒计时：恢复战局内 HUD').join('倒计时：恢复战局内 HUD');
fs.writeFileSync(p, s);
const left = (s.match(/SetupSetting\([^,]+,\s*"[^"]*[A-Za-z]{3,}[^"]*"/g) || []).length;
console.log('translated', n, 'labels; english left', left);
