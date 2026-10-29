import fs from 'node:fs';
const p = 'mods/fika-47965-refactor/source/Client/Fika.Core/FikaConfig.cs';
let lines = fs.readFileSync(p,'utf8').replace(/\r\n/g,'\n').split('\n');
let dropped = 0;
const bad = /^\s*Fika\.Core\.Main\.[\w\.]*(FikaRallyGate|CutsceneBoundary)[\w\.]*/;
for (let i = 0; i < lines.length; i++) {
  if (!bad.test(lines[i])) continue;
  let j = i;
  while (!/;\s*$/.test(lines[j]) && j + 1 < lines.length) j++;
  lines.splice(i, j - i + 1);
  dropped++;
  i--;
}
fs.writeFileSync(p, lines.join('\n'));
console.log('dropped', dropped);
