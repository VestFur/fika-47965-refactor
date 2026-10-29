import fs from 'node:fs';
const f = 'mods/fika-47965-refactor/source/Client/Fika.Core/Main/GameMode/CountdownBisect.cs';
let s = fs.readFileSync(f,'utf8').replace(/\r\n/g,'\n');
const before = s.length;
// Describe() is unreferenced anywhere else
s = s.replace(/        internal string Describe\(\)\s*\{[\s\S]*?\n        \}\n/, '');
// Statement(ConfigEntry<bool>) helper (config entries were retired)
s = s.replace(/\n    \/\/\/ <summary>A statement switch[\s\S]*?\n    private static bool Statement\(ConfigEntry<bool> entry\)[\s\S]*?\n    \}\n/, '\n');
fs.writeFileSync(f, s);
console.log('bytes', before, '->', s.length);
