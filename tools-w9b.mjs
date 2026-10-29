import fs from 'node:fs';
const f = 'mods/fika-47965-refactor/source/Client/Fika.Core/Main/Components/RaidInteractionSync.cs';
let src = fs.readFileSync(f + '.w9bak', 'utf8').replace(/\r\n/g, '\n');
const lines = src.split('\n');
const idx = lines.findIndex(l => /^\s{4}private\s+static\s+bool\s+AdoptOwnController\s*\(/.test(l));
if (idx < 0) { console.log('signature not found'); process.exit(1); }
let start = idx; while (start < lines.length && !lines[start].includes('{')) start++;
let depth = 0, end = -1;
for (let i = start; i < lines.length; i++) { for (const ch of lines[i]) { if (ch === '{') depth++; else if (ch === '}') depth--; } if (depth === 0) { end = i; break; } }
const head = lines.slice(idx, start).join('\n');
const replacement = head + '\n    {\n        // (2026-10-18 refactor W3-interaction) The controller-adoption heuristic is gone: the bridge only\n        // creates and links the controllers it needs (see CutsceneBoundary) and no longer adopts a foreign one.\n        return false;\n    }';
lines.splice(idx, end - idx + 1, replacement);
fs.writeFileSync(f, lines.join('\n'));
console.log('neutralised AdoptOwnController, lines', idx + 1, '-', end + 1);
