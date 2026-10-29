import fs from 'node:fs';
import path from 'node:path';
const root = 'mods/fika-47965-refactor/source/Client/Fika.Core';
const files = [];
(function walk(d){ for (const e of fs.readdirSync(d,{withFileTypes:true})) { const p = path.join(d,e.name); if (e.isDirectory()) walk(p); else if (e.name.endsWith('.cs')) files.push(p); } })(root);
const cfgPath = path.join(root,'FikaConfig.cs');
const texts = new Map(files.map(f => [f, fs.readFileSync(f,'utf8').replace(/\r\n/g,'\n')]));
const cfg = texts.get(cfgPath);
const re = /(\w+)\s*=\s*SetupSetting\(\s*\w+\s*,\s*"([^"]+)"/g;
const rows = [];
let m;
while ((m = re.exec(cfg)) !== null) {
  const name = m[1], label = m[2];
  let consumers = 0, values = 0, filesWith = new Set();
  for (const [f, t] of texts) {
    if (f === cfgPath) continue;
    const hits = (t.match(new RegExp('\\b' + name + '\\b', 'g')) || []).length;
    if (!hits) continue;
    consumers += hits;
    values += (t.match(new RegExp(name + '\\.Value', 'g')) || []).length;
    filesWith.add(path.basename(f));
  }
  rows.push({ name, label, consumers, values, files: [...filesWith].slice(0,4) });
}
const wanted = /Online Player|Online Players|Load Sync|Transit:|Pose Write|Mirror Offline|Ambient Bot|Ground Loot|Plant|Strict Inventory|MapFixedTime|Require All Teammates|Suppress Co-Located|Abort When|CountdownFallback|Restore In-Raid HUD|Play Opening|Sentry|Cutscene|Rally/i;
const sel = rows.filter(r => wanted.test(r.label));
console.log(JSON.stringify(sel, null, 1));
