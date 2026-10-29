import fs from 'node:fs';
const src = 'mods/fika-47965/source/Client/Fika.Core/FikaConfig.cs';
const dst = 'mods/fika-47965-refactor/source/Client/Fika.Core/FikaConfig.cs';
let s = fs.readFileSync(src, 'utf8').replace(/\r\n/g, '\n');
const fields = {
  RearmAfterRefusal: 'true', ReassertPresenceOnRefusal: 'true', EnsureTransferStash: 'true',
  ClientEnsureTransferStash: 'true', PressFallback: 'true', PressFallbackLimit: '8',
  PressFallbackNativeExit: 'true', NativeAnswerOnRefusal: 'true', PressNudge: 'true',
  PressNudgeIntervalSeconds: '5f', PressNudgeLimit: '4', HideCountdownWithoutOwnEntry: 'true',
  ActivateTransitTimerPanel: 'true'
};
s = s.replace(/^\s*Fika\.Core\.Main\.Utils\.TransitRecovery\.\w+\s*=\s*[^;]+;\s*\n/gm, '');
s = s.replace(/^\s*TransitRecovery\.Reset\([^;]*\);\s*\n/gm, '');
s = s.replace(/TransitRecovery\.Join\(([^)]*)\)/g, 'string.Join(",", $1)');
for (const [k, v] of Object.entries(fields)) s = s.split('TransitRecovery.' + k).join(v);
s = s.replace(/^\s*\w+\.SettingChanged \+= \(_, _\) =>\s*\n/gm, '');
s = s.replace(/^\s*\/\/ \(analysis\/config-audit-2\) The retired 'Transit: (Reassert Presence After Refusal|Client Transfer Stash)'[\s\S]*?\n/gm, '');
s = s.replace(/trueLimit/g, '8').replace(/trueIntervalSeconds/g, '5f').replace(/trueNativeExit/g, 'true');
fs.writeFileSync(dst, s);
console.log('restored+transformed, size', s.length);
