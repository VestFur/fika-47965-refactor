import fs from 'node:fs';
const f = 'mods/fika-47965-refactor/source/Client/Fika.Core/Main/Components/FikaFinalMissionDirector.cs';
let src = fs.readFileSync(f, 'utf8').replace(/\r\n/g, '\n');
fs.writeFileSync(f + '.w13bak', src);
function dropIfBlock(text, pred) {
  const lines = text.split('\n');
  const idx = lines.findIndex(pred);
  if (idx < 0) return { text, ok: false };
  const inline = lines[idx].includes('{') && lines[idx].trim().endsWith('}');
  if (inline) { lines.splice(idx, 1); return { text: lines.join('\n'), ok: true }; }
  let start = idx; while (start < lines.length && !lines[start].includes('{')) start++;
  if (start >= lines.length) return { text, ok: false };
  let depth = 0, end = -1;
  for (let i = start; i < lines.length; i++) { for (const ch of lines[i]) { if (ch === '{') depth++; else if (ch === '}') depth--; } if (depth === 0) { end = i; break; } }
  if (end < 0) return { text, ok: false };
  lines.splice(idx, end - idx + 1);
  return { text: lines.join('\n'), ok: true };
}
let out = src, n = 0;
for (let i = 0; i < 6; i++) {
  const r = dropIfBlock(out, l => /^\s*if \(EndingRepair\b/.test(l));
  if (!r.ok) break;
  out = r.text; n++;
}
// WatchLocalEnding: drop when unreferenced
const refs = (out.match(/WatchLocalEnding/g) || []).length;
if (refs === 1) {
  const r2 = dropIfBlock(out, l => /^\s*private static void WatchLocalEnding\s*\(/.test(l));
  if (r2.ok) { out = r2.text; console.log('dropped WatchLocalEnding'); }
}
fs.writeFileSync(f, out);
console.log('EndingRepair blocks dropped:', n, '| bytes', src.length, '->', out.length);
