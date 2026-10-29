import fs from 'node:fs';
const p = 'mods/fika-47965-refactor/source/Client/Fika.Core/FikaConfig.cs';
let s = fs.readFileSync(p,'utf8').replace(/\r\n/g,'\n');
const dict = [
 ['Online Player Counter','在线玩家计数'],['Online Players Fast Refresh','在线玩家快速刷新'],
 ['Online Players Refresh Seconds','可见刷新间隔（秒）'],['Online Players Idle Refresh Seconds','不可见刷新间隔（秒）'],
 ['Enable Online Players','启用在线玩家'],['Online Players Scale','在线玩家缩放'],
 ['Load Sync: Host Peer Timeout','加载同步：主机等待同伴上限（秒）'],['Load Sync: Client Host-Wait Ceiling','加载同步：客机等待主机上限（秒）'],
 ['Load Sync: Client Transit Reload Ceiling','加载同步：客机转移重载上限（秒）'],['Load Sync: Silent Warning Seconds','加载同步：无包警告间隔（秒）'],
 ['Transit: Client Reload Hold','转移：客机重载等待上限（秒）'],['Pose Write Guards','姿态写入守卫'],
 ['Spawn: Mirror Offline Flags','出生：镜像离线标志'],['Spawn: Ambient Bot Cap Fallback','出生：环境杂兵兜底上限'],
 ['Ground Loot Pickup Bypass','地面物资绕过原生拾取'],['Ground Loot Pickup Auto Fallback','卡拾取自动回退'],
 ['Plant Interaction Fix','安放交互修复'],['Plant Consumes Item','安放消耗物品'],['Plant Consume On Client','客机转发消耗'],['Plant Relay Guard','主机中继守卫'],
 ['Strict Inventory Sync','严格库存同步'],['Honor Map Fixed Time','遵从地图固定时间'],
 ['Transit: Require All Teammates Ready','转移：需要所有队友就绪'],['Transit: Suppress Co-Located Exfiltration','转移：抑制同位撤离点'],
 ['Transit: Abort When Someone Leaves','转移：有人离开则取消'],['Transit: Countdown Fallback','转移：倒计时兜底'],
 ['Countdown: Restore In-Raid HUD','倒计时：恢复战局内 HUD'],['Countdown: Play Opening Countdown','倒计时：播放开局倒计时'],
 ['Sentry: Sync Card Access','哨兵：同步刷卡放行'],['Cutscene: Native Tick Hook','过场：原生每帧钩子'],
 ['Rally: Skip Vote Timeout','集合：跳过投票超时（秒）'],['Transit: Activate Countdown Panel','转移：唤醒倒计时面板'],
 ['Transit: Per-Player Transfer Stash','转移：按玩家转移容器'],['Transit: Rearm After Refusal','转移：被拒后重新就绪'],
 ['Keypad: Relay Client Submit','密码门：转发客机提交'],['Keypad: Repair Local Controller','密码门：修复本地控制器'],
 ['Enabled','启用'],['Disabled','禁用'],['Seconds','秒'],['Timeout','超时'],['Interval','间隔']
];
s = s.replace(/SetupSetting\(\s*([A-Za-z_][\w\.]*)\s*,\s*"([^"]+)"/g, (all, header, label) => {
  if (!/[A-Za-z]/.test(label)) return all;              // already Chinese
  let t = label;
  for (const [en, zh] of dict) t = t.split(en).join(zh);
  // remaining ascii words: keep only Chinese part before '|' if present
  const bar = label.indexOf('|');
  if (bar >= 0) t = label.slice(bar + 1).trim();
  if (/[A-Za-z]{3,}/.test(t)) t = label.includes('|') ? label.split('|')[1].trim() : t;
  return 'SetupSetting(' + header + ', "' + t + '"';
});
fs.writeFileSync(p, s);
const left = (s.match(/SetupSetting\([^,]+,\s*"[^"]*[A-Za-z]{3,}[^"]*"/g) || []).length;
console.log('labels with english left:', left);
