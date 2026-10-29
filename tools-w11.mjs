import fs from 'node:fs';
const files = [
  'mods/fika-47965-refactor/source/Client/Fika.Core/Main/ClientClasses/ClientTransitController.cs',
  'mods/fika-47965-refactor/source/Client/Fika.Core/Main/HostClasses/FikaHostTransitController.cs'
];
function removeIfBlock(text, cond) {
  const lines = text.split('\n');
  const idx = lines.findIndex(l => l.trim() === cond);
  if (idx < 0) return { text, ok: false };
  // find opening brace
  let start = idx;
  while (start < lines.length && !lines[start].includes('{')) start++;
  if (start >= lines.length) return { text, ok: false };
  let depth = 0, end = -1;
  for (let i = start; i < lines.length; i++) { for (const ch of lines[i]) { if (ch === '{') depth++; else if (ch === '}') depth--; } if (depth === 0) { end = i; break; } }
  if (end < 0) return { text, ok: false };
  lines.splice(idx, end - idx + 1);
  return { text: lines.join('\n'), ok: true };
}
let total = 0;
for (const f of files) {
  let s = fs.readFileSync(f, 'utf8').replace(/\r\n/g, '\n');
  fs.writeFileSync(f + '.w11bak', s);
  const b0 = s.length;
  for (const cond of ['if (!true && !true)', 'if (!true)']) { const r = removeIfBlock(s, cond); if (r.ok) { s = r.text; total++; } }
  s = s.split('!true || ').join('');
  fs.writeFileSync(f, s);
  console.log(f.split('/').pop(), b0, '->', s.length);
}
console.log('blocks removed:', total);
