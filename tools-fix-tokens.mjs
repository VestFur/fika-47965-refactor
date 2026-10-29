import fs from 'node:fs';
import path from 'node:path';
const root = 'mods/fika-47965-refactor/source/Client/Fika.Core';
const files = [];
(function walk(d){ for (const e of fs.readdirSync(d, { withFileTypes: true })) { const p = path.join(d, e.name); if (e.isDirectory()) walk(p); else if (e.name.endsWith('.cs')) files.push(p); } })(root);
let touched = 0;
for (const f of files) {
  let s = fs.readFileSync(f, 'utf8').replace(/\r\n/g, '\n');
  const before = s;
  s = s.split('\n').map((line) => {
    let out = line;
    out = out.split('trueIntervalSeconds').join('5f');
    out = out.split('trueNativeExit').join('true');
    out = out.split('trueLimit').join(out.includes('_pressFallbacks') ? '8' : '4');
    return out;
  }).join('\n');
  if (s !== before) { fs.writeFileSync(f, s); touched++; }
}
console.log('repaired files:', touched);
