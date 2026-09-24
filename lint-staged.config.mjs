// Runs on staged files before each commit. Full checks live in check:stop and CI.
import { relative, sep } from 'node:path';

const rel = (files) =>
  files.map((f) => `"${relative(process.cwd(), f).split(sep).join('/')}"`).join(' ');
const eslint = (pkg) => (files) =>
  `node ${pkg}/node_modules/eslint/bin/eslint.js --max-warnings 0 ${rel(files)}`;

export default {
  '*.cs': (files) =>
    `dotnet format whitespace --folder --verify-no-changes --include ${rel(files)}`,
  '*.{ts,tsx,js,mjs,cjs,json,css,html,yml,yaml}': (files) => `prettier --check ${rel(files)}`,
  'web/**/*.{ts,tsx}': eslint('web'),
  'e2e/**/*.ts': eslint('e2e'),
};
