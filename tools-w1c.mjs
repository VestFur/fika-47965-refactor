import fs from 'node:fs';
const f = 'mods/fika-47965-refactor/source/Client/Fika.Core/Main/Components/FikaExtractDiag.cs';
let s = fs.readFileSync(f, 'utf8').replace(/\r\n/g, '\n');
const before = s.length;
s = s.replace(/internal static void ExtractCall\([\s\S]*?\n    \}\n/, 
  'internal static void ExtractCall(CoopGame game, string pointName, Player player, string source)\n    {\n        // (2026-10-18 refactor W1c / tracker A03) Diagnostics only: the per-extract line is gone.\n        _ = game;\n        _ = pointName;\n        _ = player;\n        _ = source;\n    }\n');
s = s.replace(/internal static void ExtractReturned\([\s\S]*?\n    \}\n/, 
  'internal static void ExtractReturned(CoopGame game, string pointName, Player player)\n    {\n        // (2026-10-18 refactor W1c / tracker A03) Diagnostics only.\n        _ = game;\n        _ = pointName;\n        _ = player;\n    }\n');
fs.writeFileSync(f, s);
console.log('changed:', before !== s.length, 'size', s.length);