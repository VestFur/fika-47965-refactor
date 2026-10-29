import fs from 'node:fs';
const files = [
 'mods/fika-47965-refactor/source/Client/Fika.Core/Main/Players/ObservedPlayer.cs',
 'mods/fika-47965-refactor/source/Client/Fika.Core/Main/ObservedClasses/ObservedMovementContext.cs',
 'mods/fika-47965-refactor/source/Client/Fika.Core/FikaConfig.cs'
];
let total = 0;
for (const f of files) {
  let s = fs.readFileSync(f,'utf8').replace(/\r\n/g,'\n');
  const before = s;
  s = s.replace(/!global::Fika\.Core\.Main\.Utils\.PoseWriteGuards\.Enabled\s*\n\s*\|\|\s*/g, '');
  s = s.replace(/!PoseWriteGuards\.Enabled\s*\|\|\s*/g, '');
  s = s.replace(/PoseWriteGuards\.Enabled\s*&&\s*/g, '');
  // config entry + wiring
  s = s.replace(/^\s*public\s+ConfigEntry<bool>\s+PoseWriteGuards\s*\{[^}]*\}\s*$/gm, '');
  const lines = s.split('\n');
  const out = [];
  for (let i = 0; i < lines.length; i++) {
    if (/^\s*PoseWriteGuards\s*=\s*SetupSetting\(/.test(lines[i])) {
      let j = i; while (!/;\s*$/.test(lines[j]) && j + 1 < lines.length) j++;
      i = j; continue;
    }
    if (/PoseWriteGuards/.test(lines[i]) && /^[\s]*[A-Za-z_.]*PoseWriteGuards/.test(lines[i]) && /=/.test(lines[i])) { continue; }
    out.push(lines[i]);
  }
  const res = out.join('\n');
  if (res !== before) { fs.writeFileSync(f, res); total++; }
}
try { fs.unlinkSync('mods/fika-47965-refactor/source/Client/Fika.Core/Main/Utils/PoseWriteGuards.cs'); console.log('deleted PoseWriteGuards.cs'); } catch {}
console.log('files touched:', total);
