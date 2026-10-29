import fs from 'node:fs';
// 1) client: replace TransitRecovery switches with the shipped constants
let p = 'mods/fika-47965-refactor/source/Client/Fika.Core/Main/ClientClasses/ClientTransitController.cs';
let t = fs.readFileSync(p,'utf8').replace(/\r\n/g,'\n');
t = t.split('TransitRecovery.HideCountdownWithoutOwnEntry').join('true');
t = t.split('TransitRecovery.ActivateTransitTimerPanel').join('true');
t = t.split('TransitRecovery.ClientEnsureTransferStash').join('true');
t = t.replace(/TransitRecovery\.\w+/g, 'true');
fs.writeFileSync(p, t);
// 2) host: restore the plain disabled-point answer
p = 'mods/fika-47965-refactor/source/Client/Fika.Core/Main/HostClasses/FikaHostTransitController.cs';
let lines = fs.readFileSync(p,'utf8').replace(/\r\n/g,'\n').split('\n');
const start = lines.findIndex(l => /^\s*if \(!point\.Enabled\)\s*$/.test(l));
if (start >= 0) {
  let i = start + 1;
  // skip '{' then find the end of our inserted block: the line that logs host enabled
  let logIdx = -1;
  for (let k = i; k < Math.min(i + 40, lines.length); k++) { if (lines[k].includes('host enabled point=')) { logIdx = k; break; } }
  if (logIdx > 0) {
    let end = logIdx + 1;
    while (end < lines.length && !/^\s*\}\s*$/.test(lines[end])) end++;
    const block = ['        if (!point.Enabled)', '        {', '            SendAnswerEvent(player, packet.pointId, TransitInteractionEvent.EType.InactivePoint, "point-disabled");', '            return;', '        }'];
    lines.splice(start, end - start + 1, ...block);
    fs.writeFileSync(p, lines.join('\n'));
    console.log('host branch restored at', start + 1);
  } else { console.log('no log line found'); }
} else { console.log('no !point.Enabled found'); }
