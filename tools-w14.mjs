import fs from 'node:fs';
import path from 'node:path';
const root = 'mods/fika-47965-refactor/source/Client/Fika.Core';
const files = [];
(function walk(d){ for (const e of fs.readdirSync(d,{withFileTypes:true})) { const p = path.join(d,e.name); if (e.isDirectory()) walk(p); else if (e.name.endsWith('.cs')) files.push(p); } })(root);
let removed = 0, skipped = 0;
for (const f of files) {
  const lines = fs.readFileSync(f,'utf8').replace(/\r\n/g,'\n').split('\n');
  const out = []; let changed = false;
  for (let i = 0; i < lines.length; i++) {
    const l = lines[i];
    const simple = /^\s*FikaBoundaries\.(Enter|Mark|Returned|Skipped)\(/.test(l);
    if (!simple) { out.push(l); continue; }
    let buf = [l], paren = 0;
    for (const ch of l) { if (ch === '(') paren++; else if (ch === ')') paren--; }
    while (paren > 0 && i + 1 < lines.length) { i++; buf.push(lines[i]); for (const ch of lines[i]) { if (ch === '(') paren++; else if (ch === ')') paren--; } }
    const text = buf.join('\n');
    if (/FikaBoundaries\.(Run|Fail)\(/.test(text)) { skipped++; out.push(...buf); continue; }
    removed++; changed = true;
  }
  if (changed) fs.writeFileSync(f, out.join('\n'));
}
console.log('boundary statements removed:', removed, 'skipped:', skipped);
