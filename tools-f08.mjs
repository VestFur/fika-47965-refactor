import fs from 'node:fs';
import path from 'node:path';
const root = 'mods/fika-47965-refactor/source/Client/Fika.Core';
const target = path.join(root, 'Main/Patches/World/PlantInteractionDiagPatch.cs');
const files = [];
(function walk(d){ for (const e of fs.readdirSync(d,{withFileTypes:true})) { const p = path.join(d,e.name); if (e.isDirectory()) walk(p); else if (e.name.endsWith('.cs')) files.push(p); } })(root);
const texts = new Map(files.map(f => [f, fs.readFileSync(f,'utf8')]));
let lines = texts.get(target).replace(/\r\n/g,'\n').split('\n');
const sig = /^\s{4}(?:internal|private|public)\s+static\s+[\w<>\[\],\.\s]+\s+(\w+)\s*\(/;
const candidates = [];
for (let i = 0; i < lines.length; i++) { const m = lines[i].match(sig); if (m) candidates.push({ name: m[1], line: i }); }
const removed = [];
for (const c of candidates) {
  let count = 0;
  for (const t of texts.values()) { const re = new RegExp('\\b' + c.name + '\\s*\\(', 'g'); const mm = t.match(re); if (mm) count += mm.length; }
  if (count !== 1) continue;
  // brace-match removal starting at the signature line
  let start = c.line;
  while (start > 0 && !lines[start].includes('{')) start++;
  if (start >= lines.length) continue;
  let depth = 0, end = -1;
  for (let i = start; i < lines.length; i++) {
    for (const ch of lines[i]) { if (ch === '{') depth++; else if (ch === '}') depth--; }
    if (depth === 0 && i >= start) { end = i; break; }
  }
  if (end < 0) continue;
  removed.push({ name: c.name, start: c.line + 1, end: end + 1, span: end - c.line + 1 });
}
removed.sort((a,b)=>b.start-a.start).forEach((r)=>{ lines.splice(r.start-1, r.span); });
fs.writeFileSync(target, lines.join('\n'));
console.log('removed', removed.length, 'dead helpers');
removed.slice(0,12).forEach(r=>console.log('  ', r.name, r.start+'-'+r.end));
