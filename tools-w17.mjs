import fs from 'node:fs';
const f = 'mods/fika-47965-refactor/source/Client/Fika.Core/Main/GameMode/BaseGameController.cs';
let s = fs.readFileSync(f,'utf8').replace(/\r\n/g,'\n');
const before = s.length;
let n = 0;
s = s.split('\n').map(line => {
  const m = line.match(/^(\s*)FikaBoundaries\.Run\([^,]+,\s*\(\)\s*=>\s*(.+)\);\s*$/);
  if (!m) return line;
  n++;
  return m[1] + m[2] + ';';
}).join('\n');
fs.writeFileSync(f, s);
console.log('unwrapped single-line Run wrappers:', n, '| bytes', before, '->', s.length);
