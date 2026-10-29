import fs from 'node:fs';
const f = 'mods/fika-47965-refactor/source/Client/Fika.Core/Main/Components/RaidInteractionSync.cs';
let src = fs.readFileSync(f,'utf8').replace(/\r\n/g,'\n');
fs.writeFileSync(f + '.w16bak', src);
function dropBlockAt(text, idx) {
  const lines = text.split('\n');
  let start = idx; while (start < lines.length && !lines[start].includes('{')) start++;
  if (start >= lines.length) return { text, ok: false };
  let depth = 0, end = -1;
  for (let i = start; i < lines.length; i++) { for (const ch of lines[i]) { if (ch === '{') depth++; else if (ch === '}') depth--; } if (depth === 0) { end = i; break; } }
  if (end < 0) return { text, ok: false };
  lines.splice(idx, end - idx + 1);
  return { text: lines.join('\n'), ok: true };
}
let out = src;
let lines = out.split('\n');
let idx = lines.findIndex(l => /else if \(AdoptOwnController\(/.test(l));
if (idx >= 0) { const r = dropBlockAt(out, idx); if (r.ok) { out = r.text; console.log('dropped else-if adopt block'); } }
// if the stub is now unreferenced, drop it
const refs = (out.match(/AdoptOwnController/g) || []).length;
if (refs <= 1) {
  lines = out.split('\n');
  const i2 = lines.findIndex(l => /private static bool AdoptOwnController\s*\(/.test(l));
  if (i2 >= 0) { const r2 = dropBlockAt(out, i2); if (r2.ok) { out = r2.text; console.log('dropped AdoptOwnController stub'); } }
}
fs.writeFileSync(f, out);
console.log('bytes', src.length, '->', out.length);
