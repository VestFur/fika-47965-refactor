import fs from 'node:fs';
import path from 'node:path';
const root = 'mods/fika-47965-refactor/source/Client/Fika.Core';
const files = [];
(function walk(d){ for (const e of fs.readdirSync(d,{withFileTypes:true})) { const p = path.join(d,e.name); if (e.isDirectory()) walk(p); else if (e.name.endsWith('.cs')) files.push(p); } })(root);
const texts = new Map(files.map(f => [f, fs.readFileSync(f,'utf8').replace(/\r\n/g,'\n')]));
const cfg = texts.get(path.join(root,'FikaConfig.cs')) || '';
const props = [...cfg.matchAll(/public\s+ConfigEntry<[^>]+>\s+(\w+)\s*\{\s*get;\s*set;\s*\}/g)].map(m=>m[1]);
const interesting = ["WriteOnlinePlayerCounter","OnlinePlayersFastRefresh","OnlinePlayersRefreshSeconds","OnlinePlayersIdleRefreshSeconds","HostPeerLoadTimeoutSeconds","ClientHostWaitCeilingSeconds","ClientTransitReloadCeilingSeconds","SilentWarningSeconds","ClientReloadHoldSeconds","PoseWriteGuards","MirrorOfflineFlags","AmbientBotCapFallback","GroundLootPickupBypass","GroundLootPickupAutoFallback","PlantInteractionFix","PlantConsumesItem","PlantConsumeOnClient","PlantRelayGuard","StrictInventorySync","HonorMapFixedTime","TransitRequireAllTeammates","TransitSuppressCoLocated","TransitAbortWhenSomeoneLeaves","TransitCountdownFallback","RestoreInRaidHud","PlayOpeningCountdown","SentrySyncCardAccess","CutsceneNativeTickHook","RallySkipVoteTimeout"];
const out = [];
for (const name of interesting) {
  let total = 0, values = 0, changed = 0, where = new Set();
  for (const [f, t] of texts) {
    const re = new RegExp('\\b' + name + '\\b', 'g');
    const hits = (t.match(re) || []).length;
    if (!hits) continue;
    total += hits;
    values += (t.match(new RegExp(name + '\\.Value', 'g')) || []).length;
    changed += (t.match(new RegExp(name + '\\.SettingChanged', 'g')) || []).length;
    if (f !== path.join(root,'FikaConfig.cs')) where.add(path.basename(f));
  }
  const defined = props.includes(name);
  out.push({ name, defined, total, values, changed, files: [...where].slice(0,6) });
}
console.log(JSON.stringify(out, null, 1));
