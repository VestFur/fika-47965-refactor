import fs from 'node:fs';
const p = 'mods/fika-47965-refactor/source/Client/Fika.Core/Main/Patches/World/SentryTerminalAccessPatches.cs';
let lines = fs.readFileSync(p,'utf8').replace(/\r\n/g,'\n').split('\n');
function dropType(name) {
  const idx = lines.findIndex(l => new RegExp('class\\s+' + name + '\\b').test(l));
  if (idx < 0) return false;
  let start = idx; while (start > 0 && !/^\s*(\/\/\/|\/\*\*)/.test(lines[start-1]) ) { if (lines[start-1].trim() === '') { start--; continue; } if (/^\s*\/\/\//.test(lines[start-1])) { start--; continue; } break; }
  while (start > 0 && /^\s*\/\//.test(lines[start-1])) start--;
  let depth = 0, end = -1;
  for (let i = idx; i < lines.length; i++) { for (const ch of lines[i]) { if (ch === '{') depth++; else if (ch === '}') depth--; } if (depth === 0) { end = i; break; } }
  if (end < 0) return false;
  lines.splice(start, end - start + 1);
  return true;
}
console.log('removed class:', dropType('Player_ExecuteInteraction_TerminalCard_Patch'));
for (const n of ['Observe','KeyOf']) {
  const idx = lines.findIndex(l => new RegExp('static[\\w\\s<>,\\[\\]\\.]*\\b' + n + '\\s*\\(').test(l));
  if (idx >= 0) {
    let start = idx; while (start > 0 && /^\s*\/\//.test(lines[start-1])) start--;
    let depth = 0, end = -1;
    for (let i = idx; i < lines.length; i++) { for (const ch of lines[i]) { if (ch === '{') depth++; else if (ch === '}') depth--; } if (depth === 0) { end = i; break; } }
    if (end > 0) { lines.splice(start, end - start + 1); console.log('removed helper', n); }
  }
}
fs.writeFileSync(p, lines.join('\n'));
