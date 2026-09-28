// Runs on staged files before each commit. Full checks live in check:stop and CI.
import { relative, sep } from 'node:path';

// Names with quotes could smuggle extra arguments into the shell command; such files are skipped.
const safe = (files) => files.filter((f) => !f.includes('"'));
const rel = (files) =>
  safe(files)
    .map((f) => `"${relative(process.cwd(), f).split(sep).join('/')}"`)
    .join(' ');
const eslint = (pkg) => (files) =>
  `node ${pkg}/node_modules/eslint/bin/eslint.js --max-warnings 0 --no-warn-ignored ${rel(files)}`;

export default {
  '*.cs': (files) =>
    `dotnet format whitespace --folder --verify-no-changes --include ${rel(files)}`,
  '*.{ts,tsx,js,mjs,cjs,json,css,html,yml,yaml}': (files) => `prettier --check ${rel(files)}`,
  'web/**/*.{ts,tsx}': eslint('web'),
  'e2e/**/*.ts': eslint('e2e'),
};
