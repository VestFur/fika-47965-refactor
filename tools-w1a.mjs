import fs from 'node:fs';
const f = 'mods/fika-47965-refactor/source/Client/Fika.Core/Main/Utils/LoadSync.cs';
let s = fs.readFileSync(f, 'utf8').replace(/\r\n/g, '\n');
const before = s.length;
s = s.replace(/internal static void Report\(string caller, string message\)\s*\{[\s\S]*?\n    \}/, 
  'internal static void Report(string caller, string message)\n    {\n        // (2026-10-18 refactor W1 / tracker A16) Diagnostics only: the loading wait no longer writes one\n        // line per poll. The entry point stays so no call site changes; the failure line that must survive\n        // (a timeout, a peer that never came) is Warn below.\n    }');
fs.writeFileSync(f, s);
console.log('Report replaced:', before !== s.length);
console.log(s.includes('[LoadSync] " + message, caller);\n    }\n\n    /// <summary>A line that names a degraded') ? 'Warn intact' : 'check Warn');