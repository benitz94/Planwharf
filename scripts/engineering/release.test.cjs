'use strict';
const assert = require('node:assert/strict'); const fs = require('node:fs'); const os = require('node:os'); const path = require('node:path');
const { execFileSync } = require('node:child_process');
const { inspectDirectory } = require('./release-smoke.cjs');
const root = path.resolve(__dirname, '../..');
// Planwharf.exe is built by the release (launch/build-exe.cjs), not kept in git; every other launcher file is.
const tracked = execFileSync('git', ['ls-files'], { cwd: root, encoding: 'utf8' }).split('\n');
assert.equal(tracked.includes('Planwharf.exe'), false, 'the launcher binary is not kept in git');
assert.ok(tracked.includes('launch/Planwharf.cs') && tracked.includes('launch/Planwharf.Launcher.csproj'), 'its source is');
const temporary = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-release-'));
try {
  // A release folder: the tracked launcher files plus a built Planwharf.exe (here a stand-in with the Windows header).
  const release = path.join(temporary, 'Planwharf');
  for (const name of ['chat.html', 'planwharf.cjs', 'planwharf.cmd', 'planwharf.sh', 'plan.js', 'server.cjs', 'fixtures/starter-notebook.json']) {
    fs.mkdirSync(path.dirname(path.join(release, name)), { recursive: true }); fs.copyFileSync(path.join(root, name), path.join(release, name));
  }
  assert.throws(() => inspectDirectory(release), /RELEASE_MISSING: Planwharf.exe/, 'a release without the launcher is refused');
  fs.writeFileSync(path.join(release, 'Planwharf.exe'), Buffer.from('MZ fixture'));
  assert.equal(inspectDirectory(release).length, 8);
  const empty = path.join(temporary, 'empty'); fs.mkdirSync(empty);
  assert.throws(() => inspectDirectory(empty), /RELEASE_MISSING.*planwharf.cmd/);
} finally { fs.rmSync(temporary, { recursive: true, force: true }); }
console.log('PASS launcher presence, executable header and missing-file regression');
