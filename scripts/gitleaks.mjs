// npm run gitleaks -- <gitleaks args>
// Runs a pinned gitleaks release, downloading it into .tools/ on first use.
// Checksums are pinned here, so a tampered download is rejected.
import { createHash } from 'node:crypto';
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { fail, quote, root, run } from './lib.mjs';

const VERSION = '8.30.1';
const ASSETS = {
  'win32-x64': [
    'windows_x64.zip',
    'd29144deff3a68aa93ced33dddf84b7fdc26070add4aa0f4513094c8332afc4e',
  ],
  'linux-x64': [
    'linux_x64.tar.gz',
    '551f6fc83ea457d62a0d98237cbad105af8d557003051f41f3e7ca7b3f2470eb',
  ],
  'darwin-arm64': [
    'darwin_arm64.tar.gz',
    'b40ab0ae55c505963e365f271a8d3846efbc170aa17f2607f13df610a9aeb6a5',
  ],
  'darwin-x64': [
    'darwin_x64.tar.gz',
    'dfe101a4db2255fc85120ac7f3d25e4342c3c20cf749f2c20a18081af1952709',
  ],
};

const platform = `${process.platform}-${process.arch}`;
const asset = ASSETS[platform];
if (!asset) fail(`gitleaks: no pinned release for ${platform}`);

// The binary is trusted only because we downloaded and verified it. A copy committed to git
// (e.g. a stub that always exits 0) must never run instead.
const tracked = run('git', ['ls-files', '--', '.tools'], { capture: true });
if (!tracked.ok || tracked.output.trim() !== '')
  fail('gitleaks: files under .tools/ are tracked by git; refusing to run.');

const dir = join(root, '.tools', 'gitleaks', VERSION);
const exe = join(dir, process.platform === 'win32' ? 'gitleaks.exe' : 'gitleaks');

if (!existsSync(exe)) {
  const [suffix, sha256] = asset;
  const name = `gitleaks_${VERSION}_${suffix}`;
  const url = `https://github.com/gitleaks/gitleaks/releases/download/v${VERSION}/${name}`;
  process.stderr.write(`gitleaks: downloading ${name}\n`);

  const response = await fetch(url);
  if (!response.ok) fail(`gitleaks: download failed with HTTP ${response.status}`);
  const bytes = Buffer.from(await response.arrayBuffer());
  const actual = createHash('sha256').update(bytes).digest('hex');
  if (actual !== sha256) fail(`gitleaks: checksum mismatch for ${name}: ${actual}`);

  mkdirSync(dir, { recursive: true });
  const archive = join(dir, name);
  writeFileSync(archive, bytes);
  // On Windows use the system bsdtar (opens zip); GNU tar from Git Bash may shadow it on PATH.
  const tar =
    process.platform === 'win32'
      ? quote(join(process.env.SystemRoot ?? 'C:\\Windows', 'System32', 'tar.exe'))
      : 'tar';
  if (!run(tar, ['-xf', name], { cwd: dir })) fail('gitleaks: extraction failed');
  if (!existsSync(exe)) fail(`gitleaks: ${exe} not found after extraction`);
  readFileSync(exe); // fail early if unreadable
}

const args = process.argv.slice(2).map(quote);
process.exit(run(quote(exe), args) ? 0 : 1);
