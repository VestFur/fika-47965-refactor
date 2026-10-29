import fs from 'node:fs';
const f = 'mods/fika-47965-refactor/source/Client/Fika.Core/Main/GameMode/BaseGameController.cs';
let src = fs.readFileSync(f,'utf8').replace(/\r\n/g,'\n');
fs.writeFileSync(f + '.w18bak', src);
let lines = src.split('\n');
let unwrapped = 0;
for (let guard = 0; guard < 40; guard++) {
  const i = lines.findIndex(l => /FikaBoundaries\.Run\(/.test(l));
  if (i < 0) break;
  // find the lambda body opener
  let open = i;
  while (open < lines.length && !/\{\s*$/.test(lines[open]) && !lines[open].includes('{')) open++;
  if (open >= lines.length) break;
  // if the opener line is the Run line with an expression lambda, skip
  if (/=>\s*[^\{]+\}\s*\)\s*;/.test(lines[open]) && !/\{\s*$/.test(lines[open])) break;
  let depth = 0, close = -1;
  for (let k = open; k < lines.length; k++) {
    for (const ch of lines[k]) { if (ch === '{') depth++; else if (ch === '}') depth--; }
    if (depth === 0) { close = k; break; }
  }
  if (close < 0) break;
  const inner = lines.slice(open + 1, close).map(l => l.startsWith('    ') ? l.slice(4) : l);
  // the close line may carry the trailing ');'
  const after = lines[close].replace(/^\s*\}\s*\)\s*;?\s*$/, '');
  if (after.trim().length) break; // unexpected shape, stop
  let endLine = close;
  if (/^\s*\)\s*;\s*$/.test(lines[close + 1] || '')) endLine = close + 1;
  lines = [...lines.slice(0, i), ...inner, ...lines.slice(endLine + 1)];
  unwrapped++;
}
fs.writeFileSync(f, lines.join('\n'));
console.log('unwrapped Run blocks:', unwrapped, '| bytes', src.length, '->', lines.join('\n').length);
