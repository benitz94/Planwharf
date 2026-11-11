'use strict';
// Copies the Planwharf app files (the same set as the portable release) into electron/app before a build.
const fs = require('node:fs');
const path = require('node:path');
const { execFileSync } = require('node:child_process');
const { appFile } = require('../engine/app-updates.cjs');
const root = path.join(__dirname, '..'), target = path.join(__dirname, 'app');
fs.rmSync(target, { recursive: true, force: true });
const names = execFileSync('git', ['ls-files', '-z'], { cwd: root, encoding: 'utf8' }).split('\0')
  .filter(name => appFile(name) && name !== 'Planwharf.exe' && !/\.cmd$/.test(name) && !/^verify[-.]/.test(name));
for (const name of names) { const to = path.join(target, name); fs.mkdirSync(path.dirname(to), { recursive: true }); fs.copyFileSync(path.join(root, name), to); }
console.log(`Copied ${names.length} app files into electron/app.`);
