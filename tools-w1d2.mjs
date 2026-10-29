import fs from 'node:fs';
const f = 'mods/fika-47965-refactor/source/Client/Fika.Core/FikaConfig.cs';
let s = fs.readFileSync(f, 'utf8').replace(/\r\n/g, '\n');
const names = ['TransitPerPlayerTransferStash', 'TransitRearmAfterRefusal', 'TransitActivateCountdownPanel', 'TransitClientPressFallback'];
let removed = 0;
for (const n of names) {
  const re = new RegExp('\\n[^\\n]*public ConfigEntry<[^\\n]*> ' + n + ' \\{ get; set; \\}[\\s\\S]*?ref failed, headers\\);\\n', 'm');
  if (re.test(s)) { s = s.replace(re, '\n'); removed++; console.log('removed', n); } else console.log('MISS', n);
}
fs.writeFileSync(f, s);
console.log('removed:', removed);
