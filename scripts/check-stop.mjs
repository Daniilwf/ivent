// npm run check:stop
// Quick check before the agent ends a turn: build, linters, architecture tests and fast tests
// of the parts touched by uncommitted changes. With a clean tree it checks everything.
// Budget: 3 minutes.
import { quote, root, run } from './lib.mjs';

const BUDGET_SECONDS = 180;
const started = Date.now();

const status = run(
  'git',
  ['-c', 'core.quotepath=off', 'status', '--porcelain', '--untracked-files=all'],
  {
    capture: true,
  },
);
const changed = status.ok
  ? status.output
      .split('\n')
      .map((line) => line.slice(3).trim())
      .map((p) => (p.includes(' -> ') ? p.split(' -> ')[1] : p))
      .map((p) => p.replace(/^"|"$/g, ''))
      .filter(Boolean)
  : [];

const all = changed.length === 0;
const touched = (re) => all || changed.some((p) => re.test(p));

const dotnetInputs =
  /^(src\/|Directory\.(Build|Packages)\.props$|GameEvent\.slnx$|global\.json$|nuget\.config$|\.editorconfig$)/;
const areas = {
  dotnet: touched(dotnetInputs),
  web: touched(/^web\//),
  e2e: touched(/^e2e\//),
  scripts: touched(
    /^(scripts\/|package\.json$|lint-staged\.config\.mjs$|commitlint\.config\.mjs$|\.github\/)/,
  ),
};

const steps = [];
if (areas.dotnet) {
  steps.push([
    '.NET build (analyzers, warnings as errors)',
    'dotnet',
    ['build', 'GameEvent.slnx', '-nologo', '-clp:ErrorsOnly', '-v', 'q'],
  ]);
  // Full format check (style and import order too), as in CI: ~10 s for the whole solution.
  steps.push([
    '.NET formatting and code style',
    'dotnet',
    ['format', 'GameEvent.slnx', '--verify-no-changes', '--no-restore'],
  ]);
  steps.push([
    '.NET architecture and fast tests',
    'dotnet',
    [
      'test',
      'GameEvent.slnx',
      '--no-build',
      '-nologo',
      '-v',
      'q',
      '--filter',
      quote('Category!=Long&Category!=Slow'),
    ],
  ]);
}
if (areas.web) {
  steps.push(['web: types', 'npm', ['--prefix', 'web', 'run', '-s', 'typecheck']]);
  steps.push(['web: ESLint', 'npm', ['--prefix', 'web', 'run', '-s', 'lint']]);
  steps.push(['web: Vitest', 'npm', ['--prefix', 'web', 'run', '-s', 'test']]);
}
if (areas.e2e) {
  steps.push(['e2e: types', 'npm', ['--prefix', 'e2e', 'run', '-s', 'typecheck']]);
  steps.push(['e2e: ESLint', 'npm', ['--prefix', 'e2e', 'run', '-s', 'lint']]);
}
if (areas.web || areas.e2e || areas.scripts) {
  steps.push([
    'Prettier',
    'node',
    [
      quote(`${root}/node_modules/prettier/bin/prettier.cjs`),
      '--check',
      '--log-level',
      'warn',
      '.',
    ],
  ]);
}

let failed = 0;
for (const [title, command, args] of steps) {
  const t = Date.now();
  const result = run(command, args, { capture: true });
  const seconds = ((Date.now() - t) / 1000).toFixed(1);
  if (result.ok) {
    process.stdout.write(`ok   ${title} (${seconds}s)\n`);
  } else {
    failed++;
    process.stdout.write(
      `FAIL ${title} (${seconds}s)\n${result.output.trim().split('\n').slice(-60).join('\n')}\n`,
    );
  }
}

const total = (Date.now() - started) / 1000;
process.stdout.write(
  `check:stop ${failed ? 'red' : 'green'} in ${total.toFixed(1)}s (${steps.length} steps)\n`,
);
if (total > BUDGET_SECONDS) {
  process.stdout.write(`warning: over the ${BUDGET_SECONDS}s budget, narrow the fast test set\n`);
}
process.exit(failed ? 1 : 0);
