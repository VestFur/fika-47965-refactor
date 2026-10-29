import fs from 'node:fs';
const f = 'mods/fika-47965-refactor/source/Client/Fika.Core/FikaConfig.cs';
let lines = fs.readFileSync(f, 'utf8').replace(/\r\n/g, '\n').split('\n');
const names = ['TransitPerPlayerTransferStash', 'TransitRearmAfterRefusal', 'TransitActivateCountdownPanel', 'TransitClientPressFallback'];
for (const n of names) {
  // 1) property declaration
  const propIdx = lines.findIndex((l) => /public\s+ConfigEntry<[^>]*>\s+/.test(l) && l.includes(n + ' { get; set; }'));
  if (propIdx >= 0) { lines.splice(propIdx, 1); console.log('prop removed', n, propIdx + 1); } else console.log('prop MISS', n);
  // 2) SetupSetting block
  const setIdx = lines.findIndex((l) => new RegExp('^\\s*' + n + '\\s*=\\s*SetupSetting\\(').test(l));
  if (setIdx < 0) { console.log('setup MISS', n); continue; }
  let end = -1;
  for (let i = setIdx; i < Math.min(lines.length, setIdx + 120); i++) {
    if (lines[i].includes('ref failed, headers);')) { end = i; break; }
  }
  if (end < 0) { console.log('setup end MISS', n); continue; }
  lines.splice(setIdx, end - setIdx + 1);
  console.log('setup removed', n, setIdx + 1, '-', end + 1);
}
fs.writeFileSync(f, lines.join('\n'));
