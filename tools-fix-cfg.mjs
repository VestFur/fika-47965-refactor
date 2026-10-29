import fs from 'node:fs';
const f = 'mods/fika-47965-refactor/source/Client/Fika.Core/FikaConfig.cs';
let s = fs.readFileSync(f, 'utf8').replace(/\r\n/g, '\n');
const before = s;
// dangling SettingChanged handlers whose bodies were the deleted TransitRecovery assignments
s = s.replace(/^\s*\w+\.SettingChanged \+= \(_, _\) =>\s*\n/gm, '');
// the retired-switch comments that belonged to them
s = s.replace(/^\s*\/\/ \(analysis\/config-audit-2\) The retired 'Transit: (Reassert Presence After Refusal|Client Transfer Stash)'[\s\S]*?\n/gm, '');
fs.writeFileSync(f, s);
console.log('changed:', before !== s.length, 'removed chars:', before.length - s.length);
