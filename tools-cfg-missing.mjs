import fs from 'node:fs';
const p = 'mods/fika-47965-refactor/source/Client/Fika.Core/FikaConfig.cs';
const s = fs.readFileSync(p,'utf8').replace(/\r\n/g,'\n');
const props = [...s.matchAll(/public\s+ConfigEntry<[^>]+>\s+(\w+)\s*\{\s*get;\s*set;\s*\}/g)].map(m=>m[1]);
const missing = props.filter(n => !new RegExp('^\\s*' + n + '\\s*=\\s*SetupSetting\\(', 'm').test(s));
console.log('properties:', props.length, 'without setup:', missing.length);
console.log(missing.join('\n'));
