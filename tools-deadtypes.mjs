import fs from 'node:fs';
import path from 'node:path';
const root = 'mods/fika-47965-refactor/source/Client/Fika.Core';
const files = [];
(function walk(d){ for (const e of fs.readdirSync(d,{withFileTypes:true})) { const p = path.join(d,e.name); if (e.isDirectory()) walk(p); else if (e.name.endsWith('.cs')) files.push(p); } })(root);
const texts = new Map(files.map(f => [f, fs.readFileSync(f,'utf8').replace(/\r\n/g,'\n')]));
const out = [];
for (const [f, t] of texts) {
  if (t.includes(': ModulePatch')) continue;
  const m = t.match(/(?:internal|public|private)?\s*(?:static\s+|sealed\s+|abstract\s+|partial\s+)*class\s+(\w+)/);
  if (!m) continue;
  const name = m[1];
  let refs = 0;
  for (const [g, u] of texts) { if (g === f) continue; refs += (u.match(new RegExp('\\b' + name + '\\b', 'g')) || []).length; }
  if (refs === 0) out.push({ f: f.replace(root + path.sep, ''), name, lines: t.split('\n').length });
}
out.sort((a,b)=>b.lines-a.lines);
console.log('dead-type candidates:', out.length);
out.slice(0,25).forEach(o=>console.log(' ', o.lines.toString().padStart(5), o.f, '(' + o.name + ')'));
