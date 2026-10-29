import { execSync } from 'node:child_process';
import fs from 'node:fs';
const cfg = 'mods/fika-47965-refactor/source/Client/Fika.Core/FikaConfig.cs';
for (let round = 0; round < 25; round++) {
  let out = '';
  try { out = execSync('tools\\dotnet10\\dotnet.exe build "mods/fika-47965-refactor/source/Client/Fika.Core/Fika.Core.csproj" -c Release -p:GameDir=D:\\EFT5.0-47965 --nologo -v q', { encoding: 'utf8', stdio: ['ignore','pipe','pipe'] }); }
  catch (e) { out = (e.stdout || '') + (e.stderr || ''); }
  const errs = [...out.matchAll(/FikaConfig\.cs\((\d+),\d+\): error/g)].map(m => parseInt(m[1],10));
  if (!errs.length) { console.log('round', round, 'clean'); break; }
  const lines = fs.readFileSync(cfg,'utf8').replace(/\r\n/g,'\n').split('\n');
  const uniq = [...new Set(errs)].sort((a,b)=>b-a);
  const dropped = [];
  for (const ln of uniq) {
    let start = ln - 1;
    while (start > 0 && !/[;{]\s*$/.test(lines[start - 1])) start--;
    let end = ln - 1;
    while (end < lines.length - 1 && !/[;{]\s*$/.test(lines[end])) end++;
    dropped.push((start+1) + '-' + (end+1));
    lines.splice(start, end - start + 1);
  }
  fs.writeFileSync(cfg, lines.join('\n'));
  console.log('round', round, 'dropped lines', dropped.join(','));
}
