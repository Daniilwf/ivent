// npm run format:file -- <path>
// Formats and lints one file. Called by the Claude Code hook after every edit.
import { existsSync } from 'node:fs';
import { extname, resolve } from 'node:path';
import { quote, repoPath, root, run } from './lib.mjs';

const input = process.argv[2];
if (!input) {
  process.stderr.write('Usage: npm run format:file -- <path>\n');
  process.exit(1);
}

const file = resolve(root, input);
if (!existsSync(file)) process.exit(0); // deleted or moved

const path = repoPath(file);
if (path.startsWith('..')) process.exit(0); // outside the repository

const ext = extname(path).toLowerCase();
const prettierExts = new Set([
  '.ts',
  '.tsx',
  '.js',
  '.mjs',
  '.cjs',
  '.json',
  '.css',
  '.html',
  '.yml',
  '.yaml',
]);
const eslintExts = new Set(['.ts', '.tsx', '.js']);
const skipped =
  /^(docs|data|test-artifacts|\.claude)\/|node_modules\/|(^|\/)(bin|obj|dist)\/|package-lock\.json$/;

let ok = true;

if (ext === '.cs') {
  ok = run('dotnet', ['format', 'whitespace', '--folder', '--include', quote(path)]);
} else if (prettierExts.has(ext) && !skipped.test(path)) {
  ok = run('node', [
    quote(`${root}/node_modules/prettier/bin/prettier.cjs`),
    '--write',
    '--log-level',
    'warn',
    quote(path),
  ]);

  const pkg = ['web', 'e2e'].find((p) => path.startsWith(`${p}/`));
  if (ok && pkg && eslintExts.has(ext)) {
    const eslint = `${root}/${pkg}/node_modules/eslint/bin/eslint.js`;
    if (existsSync(eslint)) {
      ok = run('node', [quote(eslint), '--fix', '--max-warnings', '0', quote(file)], {
        cwd: `${root}/${pkg}`,
      });
    }
  }
}

process.exit(ok ? 0 : 1);
