// Fails if a literal hex colour appears in any .razor, .css or .cs file outside the generated token output
// (design-system.md §3: "Nobody edits a colour anywhere but tokens.json"). Use a token instead:
// var(--color-...) in CSS, or a Tailwind class generated from tokens.json.
//
//   node build/check-no-hex-colours.mjs

import { readdirSync, readFileSync } from 'node:fs';
import { dirname, extname, join, relative, sep } from 'node:path';
import { fileURLToPath } from 'node:url';

const repo = join(dirname(fileURLToPath(import.meta.url)), '..');
const extensions = new Set(['.razor', '.css', '.cs']);
const skipDirectories = new Set(['.git', 'bin', 'obj', 'node_modules']);

// The only files allowed to contain hex colours: generated from tokens.json.
const generated = new Set([
  'src/Paddockside.Design/wwwroot/tokens.css',
  'src/Paddockside.Web/wwwroot/css/app.css', // Tailwind output, built from tokens by dotnet build
]);

// #rgb, #rgba, #rrggbb or #rrggbbaa as a whole word, not an HTML entity like &#160;.
const hexColour = /(?<![&\w])#(?:[0-9a-f]{8}|[0-9a-f]{6}|[0-9a-f]{4}|[0-9a-f]{3})\b/gi;

function* files(directory) {
  for (const entry of readdirSync(directory, { withFileTypes: true })) {
    const path = join(directory, entry.name);
    if (entry.isDirectory()) {
      if (!skipDirectories.has(entry.name)) yield* files(path);
    } else if (extensions.has(extname(entry.name).toLowerCase())) {
      yield path;
    }
  }
}

const findings = [];
for (const file of files(repo)) {
  const path = relative(repo, file).split(sep).join('/');
  if (generated.has(path)) continue;
  readFileSync(file, 'utf8').split(/\r?\n/).forEach((line, index) => {
    for (const match of line.matchAll(hexColour)) findings.push(`${path}:${index + 1}  ${match[0]}`);
  });
}

if (findings.length) {
  console.error(`Literal hex colours found (${findings.length}). Define colours in src/Paddockside.Design/tokens.json and use the token:\n  ${findings.join('\n  ')}`);
  process.exit(1);
}
console.log('No literal hex colours outside generated token output.');
