'use strict';
// Builds Planwharf.exe, the Windows launcher of the portable app, from launch/Planwharf.cs with the .NET SDK.
// Works on Windows, Linux and macOS. Usage: node launch/build-exe.cjs [output folder, default: the app root]
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { execFileSync } = require('node:child_process');
const root = path.join(__dirname, '..');
const target = path.resolve(process.argv[2] || root, 'Planwharf.exe');
const build = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-launcher-'));
try {
  execFileSync('dotnet', ['build', path.join(__dirname, 'Planwharf.Launcher.csproj'), '-c', 'Release', '-o', build, '-nologo', '-v', 'q',
    '-p:BaseIntermediateOutputPath=' + path.join(build, 'obj') + path.sep], { stdio: 'inherit' });
  const exe = fs.readFileSync(path.join(build, 'Planwharf.exe'));
  if (exe.subarray(0, 2).toString('ascii') !== 'MZ') throw new Error('The built launcher is not a Windows executable.');
  fs.copyFileSync(path.join(build, 'Planwharf.exe'), target);
  console.log('Built ' + target);
} finally { fs.rmSync(build, { recursive: true, force: true }); }
