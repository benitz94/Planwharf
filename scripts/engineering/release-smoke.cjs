'use strict';
const fs = require('node:fs'); const os = require('node:os'); const path = require('node:path');
const { spawnSync } = require('node:child_process');

const required = ['chat.html', 'planwharf.cjs', 'planwharf.cmd', 'planwharf.sh', 'Planwharf.exe', 'plan.js', 'server.cjs', 'fixtures/starter-notebook.json'];
function inspectDirectory(root) {
  const missing = required.filter(name => !fs.existsSync(path.join(root, name)));
  if (missing.length) throw Error('RELEASE_MISSING: ' + missing.join(', '));
  if (fs.readFileSync(path.join(root, 'Planwharf.exe')).subarray(0, 2).toString() !== 'MZ') throw Error('Windows executable header missing');
  if (!/exec node/.test(fs.readFileSync(path.join(root, 'planwharf.sh'), 'utf8'))) throw Error('Shell launcher does not invoke Node');
  if (!/node .*planwharf\.cjs/.test(fs.readFileSync(path.join(root, 'planwharf.cmd'), 'utf8'))) throw Error('Windows launcher does not invoke Node');
  return required;
}
function inspectZip(file) {
  const result = spawnSync('python', ['-c', `import zipfile,json,sys,hashlib
with zipfile.ZipFile(sys.argv[1]) as z:
 names=z.namelist()
 assert len(names)==len(set(names)), 'Duplicate ZIP members'
 assert all(n.startswith('Planwharf/') and '..' not in n.split('/') and '\\\\' not in n for n in names), 'Unsafe ZIP path'
 assert sum(i.file_size for i in z.infolist()) < 100*1024*1024, 'ZIP too large'
 required=json.loads(sys.argv[2])
 assert all('Planwharf/'+n in names for n in required), 'Required ZIP content missing'
 manifest=json.loads(z.read('Planwharf/.planwharf-install.json'))
 assert manifest['schema']==1
 assert all('Planwharf/'+n in names and hashlib.sha256(z.read('Planwharf/'+n)).hexdigest()==h for n,h in manifest['files'].items()), 'Manifest mismatch'
 assert all(n.removeprefix('Planwharf/') in manifest['files'] or n=='Planwharf/.planwharf-install.json' for n in names), 'Unmanifested file'
 print('PASS ZIP contents and manifest hashes')`, file, JSON.stringify(required)], { encoding: 'utf8', timeout: 15000, maxBuffer: 1024 * 1024 });
  if (result.error || result.status !== 0) throw Error(result.error?.message || result.stderr);
  return result.stdout;
}
async function startup(root) {
  const { createServer } = require(path.join(root, 'server.cjs'));
  const temporary = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-release-smoke-'));
  let server;
  try {
    const planFile = path.join(temporary, 'notebook.json');
    fs.copyFileSync(path.join(root, 'fixtures/starter-notebook.json'), planFile);
    const before = fs.readFileSync(planFile);
    const result = spawnSync(process.execPath, [path.join(root, 'planwharf.cjs'), 'status', '--plan', planFile, '--attempts-dir', path.join(temporary, 'attempts')], { encoding: 'utf8', timeout: 10000, env: { PATH: process.env.PATH, SystemRoot: process.env.SystemRoot, HOME: temporary, USERPROFILE: temporary, TMPDIR: temporary } });
    if (result.error || result.status !== 0) throw Error('CLI status failed: ' + (result.error?.message || result.stderr));
    server = createServer({ planFile, attemptsDir: path.join(temporary, 'server-attempts'), models: [], refreshModels: false, fetchPlans: async () => [], askModel: () => { throw Error('Inference forbidden'); }, spawnWorker: () => { throw Error('Dispatch forbidden'); } });
    await new Promise((resolve, reject) => { server.once('error', reject); server.listen(0, '127.0.0.1', resolve); });
    const response = await fetch(`http://127.0.0.1:${server.address().port}/`, { signal: AbortSignal.timeout(5000) });
    if (!response.ok || !(await response.text()).includes('Planwharf')) throw Error('Isolated HTTP startup failed');
    if (!before.equals(fs.readFileSync(planFile))) throw Error('Smoke test changed notebook');
    console.log('PASS actual Linux Node CLI status and isolated loopback server startup; no Windows/macOS runtime claim');
  } finally { if (server?.listening) await new Promise(resolve => server.close(resolve)); fs.rmSync(temporary, { recursive: true, force: true }); }
}
module.exports = { inspectDirectory, inspectZip, startup };
if (require.main === module) (async () => { const root = path.resolve(__dirname, '../..'); inspectDirectory(root); if (process.argv[2]) console.log(inspectZip(path.resolve(process.argv[2]))); await startup(root); })().catch(error => { console.error(error.message); process.exitCode = 1; });
