import fs from 'node:fs';
const f = 'mods/fika-47965-refactor/source/Client/Fika.Core/FikaConfig.cs';
let lines = fs.readFileSync(f, 'utf8').replace(/\r\n/g, '\n').split('\n');
const names = ['TransitPerPlayerTransferStash', 'TransitRearmAfterRefusal', 'TransitActivateCountdownPanel', 'TransitClientPressFallback'];
for (const n of names) {
  const start = lines.findIndex((l) => l.includes('public ConfigEntry<') && l.includes(n + ' { get; set; }'));
  if (start < 0) { console.log('MISS start', n); continue; }
  let end = -1;
  for (let i = start; i < Math.min(lines.length, start + 80); i++) {
    if (lines[i].includes('ref failed, headers);')) { end = i; break; }
    if (i > start && /^\s*(public|private|internal)\s+(static\s+)?(class|void|[A-Za-z])/.test(lines[i]) && !lines[i].includes('ConfigDescription')) { break; }
  }
  if (end < 0) { console.log('MISS end', n, 'start', start + 1); continue; }
  const span = end - start + 1;
  const body = lines.slice(start, end + 1).join('\n');
  if (span > 60 || body.includes('class ') || body.includes('void ')) { console.log('UNSAFE', n, 'span', span); continue; }
  lines.splice(start, span);
  console.log('removed', n, 'lines', start + 1, '-', end + 1, '(span', span + ')');
}
fs.writeFileSync(f, lines.join('\n'));
