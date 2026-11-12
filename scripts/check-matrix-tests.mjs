// The test matrix check (AU12-3, docs/TESTING.md): every test docs/TEST_MATRIX.md cites must exist in the sources, so the
// rule → test links stay checkable. Cited in backticks:
//   `Dir/ClassTests.Method_name` or `ClassTests.Method_name` — the class (any partial file) declares the method;
//   `Dir/ClassTests` or `Dir/ClassTests.*` — the class exists;
//   `Method_name_with_underscores` — the name is declared in some test source (C#, Vitest or Playwright);
//   `Something.test.tsx` / `.spec.ts` — a file of that name exists.
// Wildcards inside a name (`*/FirstFinisher*Tests`, `Invariants_hold_with_proofs*`) match by prefix and suffix.
// Rows still planned (⬜ or ➖ without ✅) name tests to come and are not checked.
// Usage: npm run check:matrix. Fails with the list of names it did not find.
import { readdirSync, readFileSync } from 'node:fs';
import { basename, join } from 'node:path';
import { root } from './lib.mjs';

const skipDirs = new Set([
  'node_modules',
  'bin',
  'obj',
  'dist',
  '.git',
  'test-results',
  'playwright-report',
]);

function walk(dir, out = []) {
  for (const entry of readdirSync(dir, { withFileTypes: true })) {
    if (skipDirs.has(entry.name)) continue;
    const path = join(dir, entry.name);
    if (entry.isDirectory()) walk(path, out);
    else out.push(path);
  }
  return out;
}

const csFiles = walk(join(root, 'src')).filter((f) => f.endsWith('.cs') && /Tests[\\/]/.test(f));
const tsFiles = [...walk(join(root, 'web', 'src')), ...walk(join(root, 'e2e'))].filter((f) =>
  /\.(test|spec)\.tsx?$|[\\/]e2e[\\/].*\.ts$/.test(f),
);
const fileNames = new Set([...csFiles, ...tsFiles].map((f) => basename(f)));

/** Class name → the text of every file declaring it (partial classes spread over several files) */
const classes = new Map();
const allText = [];
for (const file of csFiles) {
  const text = readFileSync(file, 'utf8');
  allText.push(text);
  for (const match of text.matchAll(/\b(?:class|record)\s+([A-Za-z_][A-Za-z0-9_]*)/g)) {
    const list = classes.get(match[1]) ?? [];
    list.push(text);
    classes.set(match[1], list);
  }
}
for (const file of tsFiles) allText.push(readFileSync(file, 'utf8'));
const everything = allText.join('\n');

const escape = (s) => s.replace(/[.+?^${}()|[\]\\]/g, '\\$&');
/** A name, maybe with `*` wildcards, as a whole word */
const wordPattern = (name) =>
  new RegExp(
    `(?<![A-Za-z0-9_])${name.split('*').map(escape).join('[A-Za-z0-9_]*')}(?![A-Za-z0-9_])`,
  );
const declaredIn = (texts, name) => texts.some((t) => wordPattern(name).test(t));
const classNames = [...classes.keys()];
const classExists = (name) =>
  name.includes('*') ? classNames.some((c) => wordPattern(name).test(c)) : classes.has(name);

const matrix = readFileSync(join(root, 'docs', 'TEST_MATRIX.md'), 'utf8');
const missing = [];
let checked = 0;
for (const [lineIndex, line] of matrix.split('\n').entries()) {
  if (/[⬜➖]/u.test(line) && !line.includes('✅')) continue;
  for (const [, raw] of line.matchAll(/`([^`\s]+)`/g)) {
    const token = raw.replace(/[(),;:]+$/, '');
    let problem = null;
    if (/\.(test|spec)\.tsx?$/.test(token)) {
      checked++;
      const name = basename(token);
      const found = name.includes('*')
        ? [...fileNames].some((f) =>
            new RegExp(`^${name.split('*').map(escape).join('.*')}$`).test(f),
          )
        : fileNames.has(name);
      if (!found) problem = 'no such test file';
    } else if (/^([A-Za-z.]+\/)*[A-Z][A-Za-z0-9*]*Tests(\.[A-Za-z0-9_*]+)?$/.test(token)) {
      // `Web.Tests/Auth/AccountManagementTests.Name`: the class is the last path segment
      const last = token.split('/').pop();
      const [cls, method] = last.split('.');
      checked++;
      if (!classExists(cls)) problem = 'no such test class';
      else if (method && method !== '*' && /_/.test(method)) {
        const texts = cls.includes('*') ? allText : classes.get(cls);
        if (!declaredIn(texts, method)) problem = `no such test in ${cls}`;
      }
    } else if (/^[A-Z][A-Za-z0-9]*(_[A-Za-z0-9*]+){2,}$/.test(token)) {
      checked++;
      if (!wordPattern(token).test(everything)) problem = 'no such test';
    }
    if (problem) missing.push(`  docs/TEST_MATRIX.md:${lineIndex + 1}  ${token} — ${problem}`);
  }
}

if (missing.length > 0) {
  console.error(
    `TEST_MATRIX cites ${missing.length} test(s) the sources do not have:\n${missing.join('\n')}`,
  );
  process.exit(1);
}
console.log(`TEST_MATRIX: ${checked} cited tests found in the sources.`);
