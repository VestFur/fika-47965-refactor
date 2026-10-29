import fs from 'node:fs';
const cfgPath = 'mods/fika-47965-refactor/source/Client/Fika.Core/FikaConfig.cs';
let cfg = fs.readFileSync(cfgPath,'utf8').replace(/\r\n/g,'\n');
const targets = ['StrictInventorySync','GroundLootPickupBypass','GroundLootPickupAutoFallback','MirrorOfflineSpawnFlags','EnableAmbientBotCapFallback','RallySkipVoteTimeoutSeconds','TransitCountdownFallbackSeconds','TransitSuppressCoLocatedExits','CutsceneNativeTickHook','WriteOnlinePlayerCounter','TransitAbortWhenSomeoneLeaves'];
const removed = [];
for (const name of targets) {
  const before = cfg.length;
  // property declaration
  cfg = cfg.replace(new RegExp('\\n\\s*public\\s+ConfigEntry<[^>]+>\\s+' + name + '\\s*\\{[^}]*\\}\\n', 'g'), '\n');
  // setup block: NAME = SetupSetting( ... );
  const reSetup = new RegExp('\\n\\s*' + name + '\\s*=\\s*SetupSetting\\([\\s\\S]*?\\n\\s*\\);\\n', 'g');
  cfg = cfg.replace(reSetup, '\n');
  // wiring lines
  cfg = cfg.split('\n').filter(l => !new RegExp('\\b' + name + '\\b').test(l) || /^\s*\/\//.test(l)).join('\n');
  if (cfg.length !== before) removed.push(name);
}
fs.writeFileSync(cfgPath, cfg);
console.log('removed from FikaConfig:', removed.join(','));
const rest = targets.filter(t => cfg.includes(t));
console.log('still present:', rest.join(',') || '(none)');
