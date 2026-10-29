import fs from 'node:fs';
import path from 'node:path';
const root = 'mods/fika-47965-refactor/source/Client/Fika.Core';
const files = [];
(function walk(d){ for (const e of fs.readdirSync(d,{withFileTypes:true})) { const p = path.join(d,e.name); if (e.isDirectory()) walk(p); else if (e.name.endsWith('.cs')) files.push(p); } })(root);
const texts = new Map(files.map(f => [f, fs.readFileSync(f,'utf8').replace(/\r\n/g,'\n')]));
const sig = /^\s{4}private\s+static\s+[\w<>\[\],\.\?]+\s+(\w+)\s*\(/;
let totalRemoved = 0; const report = [];
for (const [file, text] of texts) {
  if (text.includes(': ModulePatch')) continue;          // Harmony entry points: never touch
  let lines = text.split('\n');
  const candidates = [];
  for (let i = 0; i < lines.length; i++) { const m = lines[i].match(sig); if (m) candidates.push({ name: m[1], line: i }); }
  const removed = [];
  for (const c of candidates) {
    let count = 0;
    for (const t of texts.values()) { const mm = t.match(new RegExp('\\b' + c.name + '\\s*\\(', 'g')); if (mm) count += mm.length; }
    if (count !== 1) continue;
    let quoted = false;
    for (const t of texts.values()) { if (t.includes('"' + c.name + '"')) { quoted = true; break; } }
    if (quoted) continue;
    let start = c.line;
    while (start > 0 && !lines[start].includes('{')) start++;
    if (start >= lines.length) continue;
    let depth = 0, end = -1;
    for (let i = start; i < lines.length; i++) {
      for (const ch of lines[i]) { if (ch === '{') depth++; else if (ch === '}') depth--; }
      if (depth === 0 && i >= start) { end = i; break; }
    }
    if (end < 0 || end - c.line > 120) continue;
    removed.push({ name: c.name, start: c.line + 1, end: end + 1, span: end - c.line + 1 });
  }
  if (!removed.length) continue;
  removed.sort((a,b)=>b.start-a.start).forEach(r=>lines.splice(r.start-1, r.span));
  fs.writeFileSync(file, lines.join('\n'));
  totalRemoved += removed.length;
  report.push(file.replace(root + path.sep, '') + ': ' + removed.map(r=>r.name).join(','));
}
console.log('total removed:', totalRemoved);
report.slice(0,40).forEach(r=>console.log(' ', r));
