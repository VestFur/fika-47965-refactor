import fs from 'node:fs';
const src = 'mods/fika-47965/source/Client/Fika.Core/FikaConfig.cs';
const dst = 'mods/fika-47965-refactor/source/Client/Fika.Core/FikaConfig.cs';
let cfg = fs.readFileSync(src,'utf8').replace(/\r\n/g,'\n');
const targets = ['StrictInventorySync','GroundLootPickupBypass','GroundLootPickupAutoFallback','MirrorOfflineSpawnFlags','EnableAmbientBotCapFallback','RallySkipVoteTimeoutSeconds','TransitCountdownFallbackSeconds','TransitSuppressCoLocatedExits','CutsceneNativeTickHook','WriteOnlinePlayerCounter','TransitAbortWhenSomeoneLeaves'];
function dropSetup(text, name) {
  const lines = text.split('\n');
  const idx = lines.findIndex(l => new RegExp('^\\s*' + name + '\\s*=\\s*SetupSetting\\(').test(l));
  if (idx < 0) return text;
  let depth = 0, end = -1;
  for (let i = idx; i < lines.length; i++) {
    for (const ch of lines[i]) { if (ch === '(') depth++; else if (ch === ')') depth--; }
    if (depth === 0 && lines[i].trimEnd().endsWith(';')) { end = i; break; }
  }
  if (end < 0) return text;
  lines.splice(idx, end - idx + 1);
  return lines.join('\n');
}
for (const name of targets) {
  cfg = cfg.replace(new RegExp('^\\s*public\\s+ConfigEntry<[^>]+>\\s+' + name + '\\s*\\{[^}]*\\}\\s*$', 'gm'), '');
  cfg = dropSetup(cfg, name);
  cfg = cfg.split('\n').filter(l => !new RegExp('^\\s*[A-Za-z_][\\w\\.]*\\.[\\w\\.]*' + name + '\\s*=').test(l))
                         .filter(l => !new RegExp('^\\s*[A-Za-z_][\\w\\.]*\\.[\\w\\.]*' + name + '\\.SettingChanged').test(l))
                         .join('\n');
  cfg = cfg.replace(new RegExp('\\n{3,}', 'g'), '\n\n');
}
fs.writeFileSync(dst, cfg);
console.log('bytes', fs.statSync(dst).size);
