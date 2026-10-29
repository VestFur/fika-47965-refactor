import fs from 'node:fs';
import path from 'node:path';
const root = 'mods/fika-47965-refactor/source/Client/Fika.Core';
for (const f of ['Main/Components/FikaRallyGate.cs','Main/Patches/World/RallyGatePatches.cs']) {
  const p = path.join(root, f);
  if (fs.existsSync(p)) { fs.unlinkSync(p); console.log('deleted', f); }
}
const files = [];
(function walk(d){ for (const e of fs.readdirSync(d,{withFileTypes:true})) { const p = path.join(d,e.name); if (e.isDirectory()) walk(p); else if (e.name.endsWith('.cs')) files.push(p); } })(root);
let touched = 0, dropped = 0;
for (const f of files) {
  let lines = fs.readFileSync(f,'utf8').replace(/\r\n/g,'\n').split('\n');
  const out = [];
  for (let i = 0; i < lines.length; i++) {
    let l = lines[i];
    if (!l.includes('FikaRallyGate')) { out.push(l); continue; }
    // remove OR-terms that referenced the gate
    l = l.replace(/\s*\|\|\s*!?FikaRallyGate\.\w+\([^()]*\)/g, '');
    l = l.replace(/!FikaRallyGate\.\w+\([^()]*\)\s*\|\|\s*/g, '');
    if (!l.includes('FikaRallyGate')) { out.push(l); continue; }
    // whole statement / comment / multi-line call: drop it (consume to balanced parens)
    let paren = 0; for (const ch of l) { if (ch === '(') paren++; else if (ch === ')') paren--; }
    dropped++;
    if (paren > 0) { let p = paren; while (i + 1 < lines.length && p > 0) { i++; for (const ch of lines[i]) { if (ch === '(') p++; else if (ch === ')') p--; } dropped++; } }
  }
  const text = out.join('\n');
  if (text !== lines.join('\n')) { fs.writeFileSync(f, text); touched++; }
}
console.log('files touched:', touched, 'lines dropped:', dropped);
