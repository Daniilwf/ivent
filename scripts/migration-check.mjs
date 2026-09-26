// The migration check (J5, SPEC «CI/CD», TESTING.md «Миграции»): the previous release's demo database must go through
// this version's migrations without losing anything, and back through their rollback.
//   1. The previous release (the latest v* tag, else where this branch left main, else main's previous commit) is
//      checked out into a temporary worktree and seeds its demo season into a fresh database.
//   2. A copy of that database is migrated by this version (the same `migrate` step as a deploy) and every season is
//      checked: its log is replayed by this version's engine and compared with the stored projection.
//   3. When this version adds migrations, the copy is rolled back to the previous release's last migration and
//      checked again by the previous release's own tool: the rollback plan works on real data.
// Usage: npm run test:migrations [-- --base <git ref>]
import { cpSync, existsSync, mkdirSync, readdirSync, readFileSync, rmSync } from 'node:fs';
import { join } from 'node:path';
import { fail, quote, root, run } from './lib.mjs';

const work = join(root, 'var', 'migration-check');
const baseTree = join(work, 'base');
const migrations = join('src', 'GameEvent.Infrastructure', 'Database', 'Migrations');

function git(...args) {
  const result = run('git', args, { capture: true });
  return result.ok ? result.output.trim() : null;
}

function baseRef() {
  const at = process.argv.indexOf('--base');
  if (at >= 0) return process.argv[at + 1] ?? fail('--base needs a git ref.');
  if (process.env.MIGRATION_BASE) return process.env.MIGRATION_BASE;
  const tag = git('describe', '--tags', '--abbrev=0', '--match', 'v*');
  if (tag) return tag;
  const head = git('rev-parse', 'HEAD');
  const fork = git('merge-base', 'HEAD', 'origin/main');
  if (fork && fork !== head) return fork;
  // On main itself: the commit before the last merge
  return git('rev-parse', 'HEAD~1') ?? fail('No previous version to compare with.');
}

/** The newest migration's id in a checkout: «20260926004242_Initial» */
function lastMigration(tree) {
  return readdirSync(join(tree, migrations))
    .filter((f) => /^\d{14}_.+\.cs$/.test(f) && !f.endsWith('.Designer.cs'))
    .map((f) => f.slice(0, -3))
    .sort()
    .at(-1);
}

const database = (file) => `Data Source=${file}`;

function step(title, ok) {
  console.log(`${ok ? 'ok  ' : 'FAIL'} ${title}`);
  if (!ok) {
    cleanUp();
    process.exit(1);
  }
}

function cleanUp() {
  if (existsSync(baseTree))
    run('git', ['worktree', 'remove', '--force', quote(baseTree)], { capture: true });
}

const ref = baseRef();
const sha = git('rev-list', '-n', '1', ref) ?? fail(`Unknown git ref: ${ref}`);
console.log(`Previous version: ${ref} (${sha.slice(0, 12)})`);

cleanUp();
rmSync(work, { recursive: true, force: true });
mkdirSync(work, { recursive: true });
step(
  'check out the previous version',
  run('git', ['worktree', 'add', '--detach', quote(baseTree), sha]),
);

// 1. The previous version's demo season
const demo = join(work, 'demo.db');
const baseWeb = join(baseTree, 'src', 'GameEvent.Web');
step('build the previous version', run('dotnet', ['build', quote(baseWeb), '-nologo', '-v', 'q']));
step(
  'seed its demo season',
  run(
    'dotnet',
    ['run', '--no-build', '--no-launch-profile', '--project', quote(baseWeb), '--', 'seed-demo'],
    {
      env: { ASPNETCORE_ENVIRONMENT: 'Development', ConnectionStrings__Main: database(demo) },
    },
  ),
);

// 2. This version's migrations on a copy, then every season replayed and compared
const copy = join(work, 'migrated.db');
for (const suffix of ['', '-wal', '-shm']) {
  if (existsSync(demo + suffix)) cpSync(demo + suffix, copy + suffix);
}
const web = join(root, 'src', 'GameEvent.Web');
step('build this version', run('dotnet', ['build', quote(web), '-nologo', '-v', 'q']));
step(
  'migrate the copy (the deploy step)',
  run(
    'dotnet',
    ['run', '--no-build', '--no-launch-profile', '--project', quote(web), '--', 'migrate'],
    {
      env: { ASPNETCORE_ENVIRONMENT: 'Production', ConnectionStrings__Main: database(copy) },
    },
  ),
);
const tool = (tree) => join(tree, 'src', 'GameEvent.Tools.Import');
step(
  'every season replays intact with this version',
  run('dotnet', [
    'run',
    '--project',
    quote(tool(root)),
    '--',
    'season-check',
    '--all',
    '--db',
    quote(copy),
  ]),
);

// 3. The rollback to the previous release's schema, checked by the previous release itself
const previous = lastMigration(baseTree);
const current = lastMigration(root);
if (previous === current) {
  console.log(`No new migrations since ${ref}: nothing to roll back.`);
} else {
  step(
    `roll back to ${previous}`,
    run('dotnet', [
      'ef',
      'database',
      'update',
      previous,
      '--project',
      quote(join('src', 'GameEvent.Infrastructure')),
      '--connection',
      quote(database(copy)),
    ]),
  );
  // A tool from before J5 has no --all: then the rollback itself is what is tested
  const program = readFileSync(join(tool(baseTree), 'Program.cs'), 'utf8');
  if (program.includes('"--all"')) {
    step(
      'the previous version replays the rolled-back copy intact',
      run('dotnet', [
        'run',
        '--project',
        quote(tool(baseTree)),
        '--',
        'season-check',
        '--all',
        '--db',
        quote(copy),
      ]),
    );
  } else {
    console.log(
      `The tool of ${ref} cannot check every season: the rollback ran, its data is not replayed.`,
    );
  }
}

cleanUp();
console.log('Migrations are safe to deploy over the previous release.');
