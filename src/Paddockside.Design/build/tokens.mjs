// Generates wwwroot/tokens.css and tailwind.config.js from tokens.json (design-system.md §3).
//
//   node build/tokens.mjs           regenerate
//   node build/tokens.mjs --check   fail if the generated files are out of date (CI)
//
// Also enforces the tier rules and the contrast rules (design-system.md §2.1, §2.3); either failing stops
// generation. No dependencies beyond Node itself.

import { readFileSync, writeFileSync, existsSync } from 'node:fs';
import { dirname, join, relative } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');
const tokens = JSON.parse(readFileSync(join(root, 'tokens.json'), 'utf8'));
const header = 'GENERATED from tokens.json by build/tokens.mjs. Do not edit; edit tokens.json and regenerate.';
const errors = [];

// ---- Read the token tree ---------------------------------------------------------------------------

/** Flattens a token group into [{ path: ['a','b'], value, type }]; `$type` inherits downwards. */
function flatten(node, path = [], inheritedType) {
  const type = node.$type ?? inheritedType;
  if ('$value' in node) return [{ path, value: node.$value, type }];
  return Object.entries(node)
    .filter(([key]) => !key.startsWith('$'))
    .flatMap(([key, child]) => flatten(child, [...path, key], type));
}

const isAlias = (value) => typeof value === 'string' && /^\{[^}]+\}$/.test(value);
const aliasPath = (value) => value.slice(1, -1);

const primitives = new Map(flatten(tokens.primitive).map((t) => [`primitive.${t.path.join('.')}`, t]));

function resolvePrimitive(value, where) {
  if (!isAlias(value) || !aliasPath(value).startsWith('primitive.')) {
    errors.push(`${where}: semantic tokens must alias a primitive, got ${JSON.stringify(value)}`);
    return undefined;
  }
  const target = primitives.get(aliasPath(value));
  if (!target) errors.push(`${where}: unknown primitive ${value}`);
  return target;
}

const resolveGroup = (group, prefix) =>
  flatten(group).map((t) => {
    const where = `${prefix}.${t.path.join('.')}`;
    const target = resolvePrimitive(t.value, where);
    return { path: t.path, value: target?.value, type: target?.type };
  });

const base = resolveGroup(tokens.semantic.base, 'semantic.base');
const modes = { light: resolveGroup(tokens.semantic.light, 'semantic.light'), dark: resolveGroup(tokens.semantic.dark, 'semantic.dark') };

const baseNames = new Set(base.map((t) => `semantic.base.${t.path.join('.')}`));
const components = flatten(tokens.component).map((t) => {
  const where = `component.${t.path.join('.')}`;
  if (!isAlias(t.value) || !baseNames.has(aliasPath(t.value)))
    errors.push(`${where}: component tokens must alias a semantic.base token, got ${JSON.stringify(t.value)}`);
  return { path: t.path, target: isAlias(t.value) ? aliasPath(t.value).split('.').slice(2) : [] };
});

// Light and dark must define exactly the same names.
const names = (list) => new Set(list.map((t) => t.path.join('.')));
for (const [a, b] of [['light', 'dark'], ['dark', 'light']])
  for (const name of names(modes[a])) if (!names(modes[b]).has(name)) errors.push(`semantic.${a}.${name} has no ${b} value`);

// ---- Contrast (WCAG 2.1) ---------------------------------------------------------------------------

function luminance(hex) {
  const n = hex.replace('#', '');
  const full = n.length === 3 ? [...n].map((c) => c + c).join('') : n;
  const [r, g, b] = [0, 2, 4].map((i) => parseInt(full.slice(i, i + 2), 16) / 255)
    .map((c) => (c <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4));
  return 0.2126 * r + 0.7152 * g + 0.0722 * b;
}

const contrast = (a, b) => {
  const [hi, lo] = [luminance(a), luminance(b)].sort((x, y) => y - x);
  return (hi + 0.05) / (lo + 0.05);
};

// [foreground, background, minimum]. AAA for body text, AA for other text, 3:1 for the focus ring.
const pairs = [
  ['text.primary', 'surface.default', 7], ['text.primary', 'surface.raised', 7], ['text.primary', 'surface.sunken', 7],
  ['text.muted', 'surface.default', 4.5], ['text.muted', 'surface.raised', 4.5], ['text.muted', 'surface.sunken', 4.5],
  ['action.primary.fg', 'action.primary.bg', 4.5], ['action.primary.fg', 'action.primary.bg-hover', 4.5],
  ['action.secondary.fg', 'action.secondary.bg', 4.5], ['action.secondary.fg', 'action.secondary.bg-hover', 4.5],
  ['focus.ring', 'surface.default', 3], ['border.strong', 'surface.default', 3],
];
for (const set of ['status', 'scope', 'kind'])
  for (const name of new Set(modes.light.filter((t) => t.path[0] === set).map((t) => t.path[1])))
    pairs.push([`${set}.${name}.fg`, `${set}.${name}.bg`, 4.5], [`${set}.${name}.fg`, 'surface.default', 4.5]);

const contrastReport = [];
for (const [mode, list] of Object.entries(modes)) {
  const colour = new Map(list.filter((t) => t.type === 'color').map((t) => [t.path.join('.'), t.value]));
  for (const [fg, bg, min] of pairs) {
    if (!colour.has(fg) || !colour.has(bg)) { errors.push(`contrast check names a missing token: ${fg} / ${bg}`); continue; }
    const ratio = contrast(colour.get(fg), colour.get(bg));
    contrastReport.push({ mode, fg, bg, ratio, min });
    if (ratio < min) errors.push(`${mode}: ${fg} on ${bg} is ${ratio.toFixed(2)}:1, needs ${min}:1`);
  }
}

if (errors.length) {
  console.error(`tokens.json has ${errors.length} problem(s):\n  ${errors.join('\n  ')}`);
  process.exit(1);
}

// ---- CSS custom properties -------------------------------------------------------------------------

const cssValue = (t) => (Array.isArray(t.value) ? t.value.map((f) => (/\s/.test(f) ? `"${f}"` : f)).join(', ') : String(t.value));
const baseVar = (path) => `--${path.join('-')}`;
const colourVar = (path) => `--color-${path.join('-')}`;
const declarations = (list, indent) => list.map((t) => `${indent}${colourVar(t.path)}: ${t.value};`).join('\n');

const css = `/* ${header} */

:root {
${base.map((t) => `  ${baseVar(t.path)}: ${cssValue(t)};`).join('\n')}

${components.map((t) => `  ${baseVar(t.path)}: var(${baseVar(t.target)});`).join('\n')}

  color-scheme: light;
${declarations(modes.light, '  ')}
}

@media (prefers-color-scheme: dark) {
  :root:not([data-theme="light"]) {
    color-scheme: dark;
${declarations(modes.dark, '    ')}
  }
}

:root[data-theme="dark"] {
  color-scheme: dark;
${declarations(modes.dark, '  ')}
}
`;

// ---- Tailwind theme --------------------------------------------------------------------------------

/** Builds a nested object from paths, so `action.primary.bg` becomes { action: { primary: { bg } } }. */
function nest(entries) {
  const out = {};
  for (const [path, value] of entries) {
    let node = out;
    path.slice(0, -1).forEach((key) => (node = node[key] ??= {}));
    node[path.at(-1)] = value;
  }
  return out;
}

const ofGroup = (group) => base.filter((t) => t.path[0] === group).map((t) => [t.path.slice(1), `var(${baseVar(t.path)})`]);
const flat = (group) => Object.fromEntries(ofGroup(group).map(([path, value]) => [path.join('-'), value]));

const theme = {
  // Replaces Tailwind's palette entirely: only token colours exist, so `bg-blue-500` cannot be written.
  colors: { transparent: 'transparent', current: 'currentColor', ...nest(modes.light.map((t) => [t.path, `var(${colourVar(t.path)})`])) },
  fontFamily: { ui: 'var(--font-ui)' },
  fontSize: Object.fromEntries(ofGroup('text-size').map(([path, value]) => [path.join('-'), [value, { lineHeight: `var(--leading-${path.join('-')})` }]])),
  fontWeight: flat('weight'),
  spacing: { 0: '0', px: '1px', ...flat('space') },
  borderRadius: { none: '0', ...flat('radius') },
  boxShadow: { none: 'none', ...flat('elevation') },
  transitionDuration: flat('motion'),
  // Outlines and rings default to the focus token rather than a Tailwind colour.
  outlineColor: { DEFAULT: 'var(--color-focus-ring)', focus: 'var(--color-focus-ring)' },
  ringColor: { DEFAULT: 'var(--color-focus-ring)', focus: 'var(--color-focus-ring)' },
  extend: {
    minHeight: { control: 'var(--control-height)' },
    height: { control: 'var(--control-height)' },
    padding: { card: 'var(--card-padding)' },
    gap: { 'stream-item': 'var(--stream-item-gap)' },
  },
};

const tailwind = `// ${header}
// Colours are CSS variables from wwwroot/tokens.css, so light and dark switch without \`dark:\` variants.

/** @type {import('tailwindcss').Config} */
module.exports = {
  // Paths are relative to this file. Source folders only, never bin/ or obj/.
  content: {
    relative: true,
    files: [
      '../Paddockside.Web/{Components,Layout,Pages,Shared}/**/*.{razor,cs}',
      '../Paddockside.Web/App.razor',
      '../Paddockside.Web/wwwroot/index.html',
    ],
  },
  darkMode: ['selector', '[data-theme="dark"]'],
  theme: ${JSON.stringify(theme, null, 2).replace(/\n/g, '\n  ')},
};
`;

// ---- C# constants for emails ----------------------------------------------------------------------
// Email clients ignore CSS variables, so emails need literal values. Light mode only: most clients render email
// on a light background. Same semantic names as the CSS, in PascalCase.

const pascal = (path) => path.join('-').split('-').map((p) => p[0].toUpperCase() + p.slice(1)).join('');
// Outlook's desktop renderer mishandles rem, so email sizes are pixels (1rem = 16px).
const emailValue = (t) => {
  const v = cssValue(t);
  const rem = /^(-?\d*\.?\d+)rem$/.exec(v);
  return rem ? `${Math.round(parseFloat(rem[1]) * 16)}px` : v;
};
const csString = (value) => `"${String(value).replace(/\\/g, '\\\\').replace(/"/g, '\\"')}"`;
const csharp = `// <auto-generated>
// ${header}
// </auto-generated>

namespace Paddockside.Infrastructure.Email;

/// <summary>Token values for emails, generated from Paddockside.Design/tokens.json (light mode).</summary>
internal static class EmailTokens
{
${base.filter((t) => ['font', 'text-size', 'leading', 'space', 'radius'].includes(t.path[0]))
  .map((t) => `    public const string ${pascal(t.path)} = ${csString(emailValue(t))};`).join('\n')}

${modes.light.map((t) => `    public const string Color${pascal(t.path)} = ${csString(t.value)};`).join('\n')}
}
`;

// ---- Write or check --------------------------------------------------------------------------------

const outputs = [
  [join(root, 'wwwroot', 'tokens.css'), css],
  [join(root, 'tailwind.config.js'), tailwind],
  [join(root, '..', 'Paddockside.Infrastructure', 'Email', 'EmailTokens.g.cs'), csharp],
];
const normalise = (text) => text.replace(/\r\n/g, '\n');

if (process.argv.includes('--check')) {
  const stale = outputs.filter(([file, text]) => !existsSync(file) || normalise(readFileSync(file, 'utf8')) !== text);
  if (stale.length) {
    console.error(`Generated token files are out of date: ${stale.map(([f]) => relative(root, f)).join(', ')}\nRun: node src/Paddockside.Design/build/tokens.mjs`);
    process.exit(1);
  }
  console.log('Generated token files are up to date.');
} else {
  for (const [file, text] of outputs) writeFileSync(file, text);
  console.log(`Wrote ${outputs.map(([f]) => relative(root, f)).join(', ')}`);
}

const weakest = contrastReport.sort((a, b) => a.ratio / a.min - b.ratio / b.min).slice(0, 3);
console.log(`Contrast: ${contrastReport.length} pairs pass. Tightest: ${weakest.map((r) => `${r.mode} ${r.fg}/${r.bg} ${r.ratio.toFixed(2)}:1 (min ${r.min})`).join('; ')}`);
