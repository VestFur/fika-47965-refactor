import fs from 'node:fs';
let p = 'mods/fika-47965-refactor/source/Client/Fika.Core/Main/ClientClasses/ClientTransitController.cs';
let lines = fs.readFileSync(p,'utf8').replace(/\r\n/g,'\n').split('\n');
for (let i = 0; i < lines.length; i++) {
  if (/>=\s*true\b/.test(lines[i]) || /<=\s*true\b/.test(lines[i])) {
    lines[i] = lines[i].replace(/>=\s*true\b/, '>= 8').replace(/<=\s*true\b/, '<= 8');
    console.log('fixed line', i+1, '->', lines[i].trim().slice(0,120));
  }
}
fs.writeFileSync(p, lines.join('\n'));
p = 'mods/fika-47965-refactor/source/Client/Fika.Core/Main/HostClasses/FikaHostTransitController.cs';
lines = fs.readFileSync(p,'utf8').replace(/\r\n/g,'\n').split('\n');
const before = lines.length;
lines = lines.filter(l => !/TransitPointStateLedger/.test(l));
fs.writeFileSync(p, lines.join('\n'));
console.log('host ledger refs removed:', before - lines.length);
