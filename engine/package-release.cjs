'use strict';
const fs = require('node:fs');
const path = require('node:path');
const zlib = require('node:zlib');
const { execFileSync } = require('node:child_process');
const { appFile, digest } = require('./app-updates.cjs');
function packageRelease(root, version, output) {
  if (!/^v\d+\.\d+\.\d+$/.test(version)) throw new Error('An exact release version is required.');
  const names = execFileSync('git', ['ls-files', '-z'], { cwd: root, encoding: 'utf8' }).split('\0').filter(name => appFile(name));
  // Planwharf.exe is built from launch/Planwharf.cs by the release workflow (node launch/build-exe.cjs), not kept in git.
  if (!names.includes('Planwharf.exe') && fs.existsSync(path.join(root, 'Planwharf.exe'))) names.push('Planwharf.exe');
  const files = names.map(name => ({ path: name, sha256: digest(fs.readFileSync(path.join(root, name))), data: fs.readFileSync(path.join(root, name)).toString('base64') }));
  const manifest = { schema: 1, version, files: Object.fromEntries(files.map(file => [file.path, file.sha256])) };
  const portable = path.join(output, 'Planwharf'); fs.mkdirSync(portable, { recursive: true });
  for (const name of names) { const target = path.join(portable, name); fs.mkdirSync(path.dirname(target), { recursive: true }); fs.copyFileSync(path.join(root, name), target); }
  fs.writeFileSync(path.join(portable, '.planwharf-install.json'), JSON.stringify(manifest, null, 2) + '\n');
  fs.writeFileSync(path.join(output, 'planwharf-update.json.gz'), zlib.gzipSync(JSON.stringify({ schema: 1, version, files })));
  execFileSync('python', ['-c', 'import pathlib,zipfile,sys; root=pathlib.Path(sys.argv[1]); archive=zipfile.ZipFile(sys.argv[2],"w",zipfile.ZIP_DEFLATED); [archive.write(p,p.relative_to(root.parent)) for p in root.rglob("*") if p.is_file()]; archive.close()', portable, path.join(output, 'Planwharf-' + version + '.zip')]);
  return manifest;
}
if (require.main === module) packageRelease(path.join(__dirname, '..'), process.argv[2], path.resolve(process.argv[3]));
module.exports = { packageRelease };
