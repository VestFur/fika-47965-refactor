import fs from 'node:fs';
import path from 'node:path';
const root = 'mods/fika-47965-refactor/source/Client/Fika.Core';
const files = [];
(function walk(d){ for (const e of fs.readdirSync(d,{withFileTypes:true})) { const p = path.join(d,e.name); if (e.isDirectory()) walk(p); else if (e.name.endsWith('.cs')) files.push(p); } })(root);
const decls = new Set();
for (const f of files) {
  const t = fs.readFileSync(f,'utf8');
  for (const m of t.matchAll(/\b(?:class|struct|interface|enum)\s+(\w+)/g)) decls.add(m[1]);
}
const src = 'mods/fika-47965/source/Client/Fika.Core/FikaConfig.cs';
const dst = path.join(root,'FikaConfig.cs');
let lines = fs.readFileSync(src,'utf8').replace(/\r\n/g,'\n').split('\n');
// group into statements: a statement starts at a line whose trimmed text begins a known wiring call and ends at ';'
const out = [];
let dropped = 0;
for (let i = 0; i < lines.length; i++) {
  const line = lines[i];
  if (!/Fika\.Core\.Main\./.test(line)) { out.push(line); continue; }
  // collect the whole statement (until a line ending with ';')
  const buf = [line];
  let j = i;
  while (!/;\s*$/.test(buf[buf.length-1]) && j + 1 < lines.length) { j++; buf.push(lines[j]); }
  const text = buf.join('\n');
  const types = [...text.matchAll(/Fika\.Core\.Main(?:\.[A-Za-z_]\w*)*?\.([A-Z]\w*)\b/g)].map(m=>m[1]);
  const missing = types.filter(t => !decls.has(t) && !/^Utils$|^HostClasses$|^Components$|^GameMode$|^ClientClasses$|^Players$|^Networking$/.test(t));
  if (missing.length) { dropped++; i = j; continue; }
  out.push(...buf); i = j;
}
fs.writeFileSync(dst, out.join('\n'));
console.log('dropped statements:', dropped, 'bytes:', fs.statSync(dst).size);
