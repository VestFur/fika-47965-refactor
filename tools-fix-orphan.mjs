import fs from 'node:fs';
const p = 'mods/fika-47965-refactor/source/Client/Fika.Core/Main/ClientClasses/ClientTransitController.cs';
let lines = fs.readFileSync(p,'utf8').replace(/\r\n/g,'\n').split('\n');
// find orphan block: a line that is exactly "{"
const idx = lines.findIndex(l => l.trim() === '{' && /^\s{4}\{$/.test(l));
if (idx < 0) { console.log('no orphan'); process.exit(0); }
let depth = 0, end = -1;
for (let i = idx; i < lines.length; i++) { for (const ch of lines[i]) { if (ch === '{') depth++; else if (ch === '}') depth--; } if (depth === 0) { end = i; break; } }
// walk back over the doc comment
let start = idx;
while (start > 0 && /^\s*(\/\/\/|\*|\/\*\*)/.test(lines[start-1])) start--;
lines.splice(start, end - start + 1);
fs.writeFileSync(p, lines.join('\n'));
console.log('removed orphan block', start+1, '-', end+1);
