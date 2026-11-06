'use strict';
// Source inventory only; this does not measure contrast, focus order or rendered layout.
const fs = require('node:fs'); const path = require('node:path'); const { execFileSync } = require('node:child_process');
const root = path.resolve(__dirname, '../..');
function contrast(a, b) {
  const luminance = hex => { const rgb = hex.match(/[a-f0-9]{2}/gi).map(x => parseInt(x, 16) / 255).map(x => x <= 0.04045 ? x / 12.92 : ((x + 0.055) / 1.055) ** 2.4); return rgb[0] * 0.2126 + rgb[1] * 0.7152 + rgb[2] * 0.0722; };
  const values = [luminance(a), luminance(b)].sort((x, y) => y - x); return (values[0] + 0.05) / (values[1] + 0.05);
}
function inspect(read) {
  const html = read('chat.html'), css = read('chat.css');
  const dialogs = [...html.matchAll(/<dialog\b([^>]*)>/g)].map(match => ({ id: /\bid="([^"]+)"/.exec(match[1])?.[1], explicitAccessibleName: /aria-label(?:ledby)?=/.test(match[1]) }));
  const light = /:root\s*\{([^}]+)/.exec(css)?.[1] || '';
  const accent = /--accent:\s*(#[a-f0-9]{6})/i.exec(light)?.[1];
  const ink = /--accent-text:\s*(#[a-f0-9]{6})/i.exec(light)?.[1];
  return { lightPrimaryTokenContrast: accent && ink ? contrast(accent, ink) : null, evidence: 'static source only; no browser or assistive technology execution', dialogs, unnamedDialogs: dialogs.filter(x => !x.explicitAccessibleName).map(x => x.id), focusRulePresent: css.includes(':focus-visible'), motionPreferencePresent: css.includes('prefers-reduced-motion'), liveRegionPresent: html.includes('aria-live="polite"') };
}
if (require.main === module) {
  const ref = process.argv[2];
  if (ref && !/^[a-f0-9]{40}$/.test(ref)) throw Error('Use an exact inspected commit SHA');
  const read = file => ref ? execFileSync('git', ['show', `${ref}:${file}`], { cwd: root, encoding: 'utf8', timeout: 5000 }) : fs.readFileSync(path.join(root, file), 'utf8');
  console.log(JSON.stringify(inspect(read), null, 2));
}
module.exports = { inspect };
