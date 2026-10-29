import fs from 'node:fs';
import path from 'node:path';
const root = 'mods/fika-47965-refactor/source/Client/Fika.Core';
const files = [];
(function walk(d){ for (const e of fs.readdirSync(d,{withFileTypes:true})) { const p = path.join(d,e.name); if (e.isDirectory()) walk(p); else if (e.name.endsWith('.cs')) files.push(p); } })(root);
let total = 0;
for (const f of files) {
  if (f.endsWith('FikaBoundaries.cs')) continue;
  let src = fs.readFileSync(f,'utf8').replace(/\r\n/g,'\n');
  if (!src.includes('FikaBoundaries.Run(')) continue;
  fs.writeFileSync(f + '.w19bak', src);
  let lines = src.split('\n');
  for (let guard = 0; guard < 40; guard++) {
    const i = lines.findIndex(l => /FikaBoundaries\.Run\(/.test(l));
    if (i < 0) break;
    // single-line expression form
    const single = lines[i].match(/^(\s*)FikaBoundaries\.Run\([^,]+,\s*\(\)\s*=>\s*(.+)\);\s*$/);
    if (single) { lines.splice(i, 1, single[1] + single[2] + ';'); total++; continue; }
    let open = i; while (open < lines.length && !lines[open].includes('{')) open++;
    if (open >= lines.length) break;
    let depth = 0, close = -1;
    for (let k = open; k < lines.length; k++) { for (const ch of lines[k]) { if (ch === '{') depth++; else if (ch === '}') depth--; } if (depth === 0) { close = k; break; } }
    if (close < 0) break;
    if (!/^\s*\}\s*\)\s*;?\s*$/.test(lines[close])) break;
    const inner = lines.slice(open + 1, close).map(l => l.startsWith('    ') ? l.slice(4) : l);
    let endLine = close;
    if (/^\s*\)\s*;\s*$/.test(lines[close + 1] || '')) endLine = close + 1;
    lines = [...lines.slice(0, i), ...inner, ...lines.slice(endLine + 1)];
    total++;
  }
  fs.writeFileSync(f, lines.join('\n'));
}
console.log('Run wrappers unwrapped:', total);
