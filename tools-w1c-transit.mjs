import fs from 'node:fs';
import path from 'node:path';
const root = 'mods/fika-47965-refactor/source/Client/Fika.Core';
const files = [];
(function walk(d){ for (const e of fs.readdirSync(d, { withFileTypes: true })) { const p = path.join(d, e.name); if (e.isDirectory()) walk(p); else if (e.name.endsWith('.cs')) files.push(p); } })(root);
const fields = {
  RearmAfterRefusal: 'true', ReassertPresenceOnRefusal: 'true', EnsureTransferStash: 'true',
  ClientEnsureTransferStash: 'true', PressFallback: 'true', PressFallbackLimit: '8',
  PressFallbackNativeExit: 'true', NativeAnswerOnRefusal: 'true', PressNudge: 'true',
  PressNudgeIntervalSeconds: '5f', PressNudgeLimit: '4', HideCountdownWithoutOwnEntry: 'true',
  ActivateTransitTimerPanel: 'true'
};
let touched = 0, removedAssign = 0, removedReset = 0;
for (const f of files) {
  if (f.endsWith('TransitRecovery.cs')) continue;
  let s = fs.readFileSync(f, 'utf8').replace(/\r\n/g, '\n');
  const before = s;
  s = s.replace(/^\s*Fika\.Core\.Main\.Utils\.TransitRecovery\.\w+\s*=\s*[^;]+;\s*\n/gm, () => { removedAssign++; return ''; });
  s = s.replace(/^\s*TransitRecovery\.Reset\([^;]*\);\s*\n/gm, () => { removedReset++; return ''; });
  s = s.replace(/TransitRecovery\.Join\(([^)]*)\)/g, 'string.Join(",", $1)');
  for (const [k, v] of Object.entries(fields)) {
    s = s.split('TransitRecovery.' + k).join(v);
  }
  if (s !== before) { fs.writeFileSync(f, s); touched++; }
}
fs.unlinkSync(path.join(root, 'Main', 'Utils', 'TransitRecovery.cs'));
console.log('files touched:', touched, 'assignments removed:', removedAssign, 'resets removed:', removedReset);
