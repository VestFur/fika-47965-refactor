import fs from 'node:fs';
import path from 'node:path';
const root = 'mods/fika-47965-refactor/source/Client/Fika.Core';
const files = [];
(function walk(d){ for (const e of fs.readdirSync(d,{withFileTypes:true})) { const p = path.join(d,e.name); if (e.isDirectory()) walk(p); else if (e.name.endsWith('.cs')) files.push(p); } })(root);
let touched = 0, dropped = 0;
for (const f of files) {
  const lines = fs.readFileSync(f,'utf8').replace(/\r\n/g,'\n').split('\n');
  const out = [];
  for (let i = 0; i < lines.length; i++) {
    const l = lines[i];
    if (!l.includes('CutsceneBoundary')) { out.push(l); continue; }
    let paren = 0;
    for (const ch of l) { if (ch === '(') paren++; else if (ch === ')') paren--; }
    // a whole line comment mentioning it is harmless, but drop it too for cleanliness
    dropped++;
    if (paren > 0) { // multi-line call: consume until balanced
      let p = paren;
      while (i + 1 < lines.length && p > 0) { i++; for (const ch of lines[i]) { if (ch === '(') p++; else if (ch === ')') p--; } dropped++; }
    }
  }
  const text = out.join('\n');
  if (text !== lines.join('\n')) { fs.writeFileSync(f, text); touched++; }
}
console.log('files touched:', touched, 'lines dropped:', dropped);
