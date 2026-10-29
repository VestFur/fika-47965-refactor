import fs from 'node:fs';
const p = 'mods/fika-47965-refactor/source/Client/Fika.Core/FikaConfig.cs';
let lines = fs.readFileSync(p,'utf8').replace(/\r\n/g,'\n').split('\n');
// 1) delete the two options entirely (property + setup block + dangling handlers)
const drop = ['CutsceneNativeTickHook','RallySkipVoteTimeoutSeconds'];
for (const name of drop) {
  lines = lines.filter(l => !new RegExp('^\\s*public\\s+ConfigEntry<[^>]+>\\s+' + name + '\\s*\\{').test(l));
  const idx = lines.findIndex(l => new RegExp('^\\s*' + name + '\\s*=\\s*SetupSetting\\(').test(l));
  if (idx >= 0) {
    let depth = 0, end = -1;
    for (let i = idx; i < lines.length; i++) {
      for (const ch of lines[i]) { if (ch === '(') depth++; else if (ch === ')') depth--; }
      if (depth === 0 && lines[i].trimEnd().endsWith(';')) { end = i; break; }
    }
    if (end > idx) lines.splice(idx, end - idx + 1);
  }
  const h = lines.findIndex(l => new RegExp('^\\s*' + name + '\\.SettingChanged').test(l));
  if (h >= 0) lines.splice(h, 1);
  lines = lines.filter(l => !new RegExp('^\\s*[A-Za-z_][\\w\\.]*\\b' + name + '\\b\\s*=').test(l));
}
// 2) any other dangling handler whose next non-empty line is not a statement body
let removed = 0;
for (let i = 0; i < lines.length; i++) {
  if (!/SettingChanged\s*\+=\s*\(_,\s*_\)\s*=>\s*$/.test(lines[i])) continue;
  let j = i + 1;
  while (j < lines.length && (lines[j].trim() === '' || lines[j].trim().startsWith('//'))) j++;
  const next = (lines[j] || '').trim();
  if (!next || /^[A-Za-z_][\w\.]*\s*=\s*SetupSetting\(/.test(next)) { lines.splice(i, 1); removed++; i--; }
}
fs.writeFileSync(p, lines.join('\n'));
console.log('dangling handlers removed:', removed);
