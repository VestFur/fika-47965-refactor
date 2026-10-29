import fs from 'node:fs';
import path from 'node:path';
const root = 'mods/fika-47965-refactor/source/Client/Fika.Core';
const dirs = ['Main/ClientClasses', 'Main/BaseClasses'];
const files = [];
function walk(d){ for (const e of fs.readdirSync(d,{withFileTypes:true})) { const p = path.join(d,e.name); if (e.isDirectory()) walk(p); else if (e.name.endsWith('.cs')) files.push(p); } }
for (const d of dirs) walk(path.join(root, d));
const all = [];
(function a(d){ for (const e of fs.readdirSync(d,{withFileTypes:true})) { const p = path.join(d,e.name); if (e.isDirectory()) a(p); else if (e.name.endsWith('.cs')) all.push(p); } })(root);
const texts = new Map(all.map(f => [f, fs.readFileSync(f,'utf8').replace(/\r\n/g,'\n')]));
const sig = /^\s{4}private\s+static\s+[\w<>\[\],\.\?]+\s+(\w+)\s*\(/;
let removed = 0;
for (const f of files) {
  const text = texts.get(f);
  if (text.includes(': ModulePatch')) continue;
  let lines = text.split('\n');
  const cands = [];
  for (let i = 0; i < lines.length; i++) { const m = lines[i].match(sig); if (m) cands.push({ name: m[1], line: i }); }
  const del = [];
  for (const c of cands) {
    let calls = 0, bare = 0;
    for (const t of texts.values()) { calls += (t.match(new RegExp('\\b' + c.name + '\\s*\\(', 'g')) || []).length; bare += (t.match(new RegExp('\\b' + c.name + '\\b', 'g')) || []).length; }
    if (calls !== 1 || bare !== calls) continue;
    let quoted = false; for (const t of texts.values()) { if (t.includes('"' + c.name + '"')) { quoted = true; break; } }
    if (quoted) continue;
    let start = c.line; while (start < lines.length && !lines[start].includes('{')) start++;
    let depth = 0, end = -1;
    for (let i = start; i < lines.length; i++) { for (const ch of lines[i]) { if (ch === '{') depth++; else if (ch === '}') depth--; } if (depth === 0) { end = i; break; } }
    if (end < 0 || end - c.line > 120) continue;
    del.push({ name: c.name, start: c.line + 1, end: end + 1, span: end - c.line + 1 });
  }
  if (!del.length) continue;
  del.sort((a,b)=>b.start-a.start).forEach(r=>lines.splice(r.start-1, r.span));
  fs.writeFileSync(f, lines.join('\n'));
  removed += del.length;
  console.log(f.replace(root + path.sep, ''), '->', del.map(r=>r.name).join(','));
}
console.log('removed:', removed);
