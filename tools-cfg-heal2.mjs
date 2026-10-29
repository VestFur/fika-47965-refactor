import { execSync } from 'node:child_process';
import fs from 'node:fs';
const p = 'mods/fika-47965-refactor/source/Client/Fika.Core/FikaConfig.cs';
function build() {
  try { return execSync('tools\\dotnet10\\dotnet.exe build "mods/fika-47965-refactor/source/Client/Fika.Core/Fika.Core.csproj" -c Release -p:GameDir=D:\\EFT5.0-47965 --nologo -v q', { encoding:'utf8', stdio:['ignore','pipe','pipe'] }); }
  catch (e) { return (e.stdout||'') + (e.stderr||''); }
}
for (let round = 0; round < 15; round++) {
  const out = build();
  const names = new Set();
  for (const m of out.matchAll(/不存在类型或命名空间名.“(\w+)”/g)) names.add(m[1]);
  for (const m of out.matchAll(/“(\w+)”未包含“(\w+)”的定义/g)) names.add(m[1]);
  const cfgErrs = (out.match(/FikaConfig\.cs\(\d+,\d+\): error/g) || []).length;
  if (!cfgErrs) { console.log('round', round, 'clean (config errors 0)'); break; }
  if (!names.size) { console.log('round', round, 'no names extracted, cfgErrs', cfgErrs); break; }
  let lines = fs.readFileSync(p,'utf8').replace(/\r\n/g,'\n').split('\n');
  let dropped = 0;
  for (const n of names) {
    const re = new RegExp('^\\s*Fika\\.Core\\.Main\\.[\\w\\.]*\\b' + n + '\\b');
    for (let i = 0; i < lines.length; i++) {
      if (!re.test(lines[i])) continue;
      let j = i; while (!/;\s*$/.test(lines[j]) && j + 1 < lines.length) j++;
      lines.splice(i, j - i + 1); dropped++; i--;
    }
  }
  fs.writeFileSync(p, lines.join('\n'));
  console.log('round', round, 'names', [...names].join(','), 'dropped', dropped);
}
