'use strict';
const fs = require('node:fs'), os = require('node:os'), path = require('node:path');
const { spawnSync } = require('node:child_process');
const assert = require('node:assert/strict');
const { packageRelease } = require('../../engine/package-release.cjs');
const { inspectZip, inspectDirectory, startup } = require('./release-smoke.cjs');
(async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-package-smoke-'));
  try {
    packageRelease(path.resolve(__dirname, '../..'), 'v0.0.0', dir); // Disposable version fixture, no tag or source version mutation.
    const packaged = path.join(dir, 'Planwharf');
    inspectDirectory(packaged);
    console.log(inspectZip(path.join(dir, 'Planwharf-v0.0.0.zip')).trim());
    await startup(packaged);
    const bad = path.join(dir, 'missing.zip');
    const make = spawnSync('python', ['-c', "import zipfile,sys; z=zipfile.ZipFile(sys.argv[1],'w'); z.writestr('Planwharf/chat.html','fixture'); z.close()", bad], { timeout: 5000 });
    if (make.error || make.status !== 0) throw Error('Could not create negative ZIP fixture');
    assert.throws(() => inspectZip(bad), /Required ZIP content missing/);
    console.log('PASS incomplete ZIP rejected');
  } finally { fs.rmSync(dir, { recursive: true, force: true }); }
})().catch(error => { console.error(error); process.exitCode = 1; });
