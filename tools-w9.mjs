import fs from 'node:fs';
const f = 'mods/fika-47965-refactor/source/Client/Fika.Core/Main/Components/RaidInteractionSync.cs';
let src = fs.readFileSync(f, 'utf8').replace(/\r\n/g, '\n');
fs.writeFileSync(f + '.w9bak', src);
function dropMethod(text, name) {
  const lines = text.split('\n');
  const idx = lines.findIndex(l => new RegExp('^\\s{4}(private|internal|public)\\s+(static\\s+)?[\\w<>\\[\\],\\.\\?\\s]+\\s+' + name + '\\s*\\(').test(l));
  if (idx < 0) return { text, ok: false };
  let start = idx; while (start < lines.length && !lines[start].includes('{')) start++;
  let depth = 0, end = -1;
  for (let i = start; i < lines.length; i++) { for (const ch of lines[i]) { if (ch === '{') depth++; else if (ch === '}') depth--; } if (depth === 0) { end = i; break; } }
  if (end < 0) return { text, ok: false };
  lines.splice(idx, end - idx + 1);
  return { text: lines.join('\n'), ok: true };
}
let out = src; const removed = [];
for (const n of ['AdoptOwnController']) { const r = dropMethod(out, n); out = r.text; if (r.ok) removed.push(n); }
out = out.split('\n').filter(l => !/AdoptOwnController\(/.test(l) || /^\s*\/\//.test(l)).join('\n');
fs.writeFileSync(f, out);
console.log('removed:', removed.join(',') || 'none', '| bytes', src.length, '->', out.length);
