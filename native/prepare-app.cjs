'use strict';
// Copies Planwharf's app files (the same ones the release and the updater use) next to the native program.
const fs = require('node:fs');
const path = require('node:path');
const { execFileSync } = require('node:child_process');
const root = path.resolve(__dirname, '..');
const target = path.resolve(process.argv[2] || path.join(__dirname, 'app'));
const { appFile } = require(path.join(root, 'engine', 'app-updates.cjs'));
const names = execFileSync('git', ['ls-files', '-z'], { cwd: root, encoding: 'utf8' }).split('\0').filter(name => name && appFile(name));
fs.rmSync(target, { recursive: true, force: true });
for (const name of names) { const to = path.join(target, name); fs.mkdirSync(path.dirname(to), { recursive: true }); fs.copyFileSync(path.join(root, name), to); }
console.log(`Copied ${names.length} app files into ${target}.`);
