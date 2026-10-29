import fs from 'node:fs';
const root = 'mods/fika-47965-refactor/source/Client/Fika.Core';
const del = [
 'Main/Patches/TransitController/HandlerTransitPointState_OnTrigger_Patch.cs',
 'Main/Utils/TransitPointStateLedger.cs'
];
for (const f of del) { try { fs.unlinkSync(root + '/' + f); console.log('deleted', f); } catch { console.log('missing', f); } }
function dropMethod(text, name) {
  const lines = text.split('\n');
  const idx = lines.findIndex(l => new RegExp('^\\s*(private|internal|public|protected)[\\w\\s<>,\\[\\]\\.]*\\b' + name + '\\s*\\(').test(l));
  if (idx < 0) return { text, ok: false };
  let start = idx; while (start < lines.length && !lines[start].includes('{')) start++;
  let depth = 0, end = -1;
  for (let i = start; i < lines.length; i++) { for (const ch of lines[i]) { if (ch === '{') depth++; else if (ch === '}') depth--; } if (depth === 0) { end = i; break; } }
  if (end < 0) return { text, ok: false };
  lines.splice(idx, end - idx + 1);
  return { text: lines.join('\n'), ok: true };
}
function dropLines(text, re) { return text.split('\n').filter(l => !re.test(l)).join('\n'); }
// 1) client transit: remove calls + methods
let f = root + '/Main/ClientClasses/ClientTransitController.cs';
let t = fs.readFileSync(f,'utf8').replace(/\r\n/g,'\n');
t = dropLines(t, /ApplyPointState\(/);
t = dropLines(t, /SendPointRequest\(/);
t = dropMethod(t, 'ApplyPointState').text;
t = dropMethod(t, 'SendPointRequest').text;
fs.writeFileSync(f, t);
// 2) inventory: remove C's additions
f = root + '/Main/ClientClasses/ClientInventoryController.cs';
t = fs.readFileSync(f,'utf8').replace(/\r\n/g,'\n');
t = dropLines(t, /MarkOperationItemsSearched\(operation\)/);
t = dropLines(t, /MarkOperationItemsSearched\(search, operation/);
t = dropLines(t, /SplitOperation>\(\) is \{ \} split\) MarkKnownTree/);
t = dropMethod(t, 'MarkOperationItemsSearched').text;
fs.writeFileSync(f, t);
// 3) my re-mark
f = root + '/Main/ClientClasses/ClientInventoryOperationHandler.cs';
t = fs.readFileSync(f,'utf8').replace(/\r\n/g,'\n');
t = dropLines(t, /could not re-register the operation items as known/);
t = dropLines(t, /MarkOperationItemsKnown\(Operation\)/);
fs.writeFileSync(f, t);
// 4) immunity calls
f = root + '/Main/Components/RaidInteractionSync.cs';
t = fs.readFileSync(f,'utf8').replace(/\r\n/g,'\n');
t = dropLines(t, /ReleaseLocalSniperImmunity\("cutscene end"\)/);
fs.writeFileSync(f, t);
f = root + '/Networking/FikaClient.RaidInteractions.cs';
t = fs.readFileSync(f,'utf8').replace(/\r\n/g,'\n');
t = dropLines(t, /ReleaseLocalSniperImmunity/);
fs.writeFileSync(f, t);
console.log('revert pass done');
