// The migration check (J5, D-200, SPEC «CI/CD», TESTING.md «Миграции»): the previous release's demo database must go
// through this version's migrations without losing anything, and back through their rollback.
//   1. The previous release (the latest v* tag before this commit, else where this branch left main, else main's
//      previous commit) is checked out into a temporary worktree and seeds its demo season into a fresh database.
//   2. A copy is migrated by this version (the same `migrate` step as a deploy). No table loses rows, no season goes,
//      and every season's log replays by this version's engine into the stored projection.
//   3. When this version adds migrations, the copy is rolled back to the previous release's last migration: its schema
//      must be the previous release's schema again, no rows lost, and the previous release's tool must replay it.
// Usage: npm run test:migrations [-- --base <git ref>]. The databases stay in var/migration-check for a look.
import { spawnSync } from 'node:child_process';
import { cpSync, existsSync, mkdirSync, readdirSync, rmSync } from 'node:fs';
import { join } from 'node:path';
import { fail, quote, root, run, runAsync } from './lib.mjs';

const work = join(root, 'var', 'migration-check');
const baseTree = join(work, 'base');
const migrations = join('src', 'GameEvent.Infrastructure', 'Database', 'Migrations');

class Failed extends Error {}

// git is an executable: no shell, so ^ and ~ in a ref reach it as they are (cmd would eat ^)
function git(...args) {
  const result = spawnSync('git', args, { cwd: root, encoding: 'utf8' });
  return result.status === 0 ? result.stdout.trim() : null;
}

function baseRef() {
  const at = process.argv.indexOf('--base');
  if (at >= 0) return process.argv[at + 1] ?? fail('--base needs a git ref.');
  if (process.env.MIGRATION_BASE) return process.env.MIGRATION_BASE;
  // Before this commit: a release commit carries its own tag, and comparing a version with itself proves nothing
  const tag = git('describe', '--tags', '--abbrev=0', '--match', 'v*', 'HEAD~1');
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
const tool = (tree) => join(tree, 'src', 'GameEvent.Tools.Import');

function step(title, ok) {
  console.log(`${ok ? 'ok  ' : 'FAIL'} ${title}`);
  if (!ok) throw new Failed(title);
}

/** A chain of commands that runs beside another one (D-672): its output is printed when it ends, then its steps */
async function chain(commands) {
  const done = [];
  for (const [title, command, args, options] of commands) {
    const started = Date.now();
    const result = await runAsync(command, args, options);
    done.push({ title, result, seconds: Math.round((Date.now() - started) / 1000) });
    if (!result.ok) break;
  }
  return done;
}

function report(done, expected) {
  for (const { title, result, seconds } of done) {
    if (!result.ok) console.log(result.output.trim());
    step(`${title} (${seconds} s)`, result.ok);
  }
  step('every step ran', done.length === expected);
}

// The previous version's checkout is thrown away after the check: its analyzers and code style checked it when it was
// built for real, so building it here without them gives the same program sooner (D-672)
const throwaway = ['-p:RunAnalyzers=false', '-p:EnforceCodeStyleInBuild=false'];

/** Rows per table, the seasons, the last migration and the schema of a database (the tool's db-census) */
function census(file) {
  // The tool is built beside the previous version (below), so it is not built again for each census
  const result = run(
    'dotnet',
    ['run', '--no-build', '--project', quote(tool(root)), '--', 'db-census', '--db', quote(file)],
    {
      capture: true,
    },
  );
  step(`read ${file.split(/[\\/]/).at(-1)}`, result.ok);
  return JSON.parse(result.output.slice(result.output.indexOf('{')));
}

/** Nothing lost from `before` to `after`: every table keeps at least its rows, every season is there */
function nothingLost(before, after, title) {
  const lost = Object.entries(before.rows)
    .filter(
      ([table, count]) => table !== '__EFMigrationsHistory' && (after.rows[table] ?? -1) < count,
    )
    .map(([table, count]) => `${table}: ${count} → ${after.rows[table] ?? 'no table'}`);
  const gone = before.seasons
    .filter((id) => !after.seasons.includes(id))
    .map((id) => `season ${id}`);
  for (const line of [...lost, ...gone]) console.log(`     ${line}`);
  step(title, lost.length === 0 && gone.length === 0);
}

function cleanUp() {
  if (existsSync(baseTree))
    run('git', ['worktree', 'remove', '--force', quote(baseTree)], { capture: true });
  // A worktree whose folder went some other way stays registered and blocks the next `worktree add`
  run('git', ['worktree', 'prune'], { capture: true });
}

const ref = baseRef();
const sha = git('rev-list', '-n', '1', ref) ?? fail(`Unknown git ref: ${ref}`);
console.log(`Previous version: ${ref} (${sha.slice(0, 12)})`);

cleanUp();
rmSync(work, { recursive: true, force: true });
mkdirSync(work, { recursive: true });
try {
  step(
    'check out the previous version',
    run('git', ['worktree', 'add', '--detach', quote(baseTree), sha]),
  );

  // 1. The previous version's demo season and 2. this version's build, side by side (D-672): they share nothing but
  // the NuGet cache. The previous version's tool is built too when its season-check will be needed (step 3).
  const demo = join(work, 'demo.db');
  const baseWeb = join(baseTree, 'src', 'GameEvent.Web');
  const web = join(root, 'src', 'GameEvent.Web');
  const rollsBack = lastMigration(baseTree) !== lastMigration(root);
  const base = [
    [
      'build the previous version',
      'dotnet',
      ['build', quote(baseWeb), '-nologo', '-v', 'q', ...throwaway],
    ],
    [
      'seed its demo season',
      'dotnet',
      ['run', '--no-build', '--no-launch-profile', '--project', quote(baseWeb), '--', 'seed-demo'],
      { env: { ASPNETCORE_ENVIRONMENT: 'Development', ConnectionStrings__Main: database(demo) } },
    ],
  ];
  if (rollsBack)
    base.push([
      "build the previous version's tool",
      'dotnet',
      ['build', quote(tool(baseTree)), '-nologo', '-v', 'q', ...throwaway],
    ]);
  const current = [
    ['build this version', 'dotnet', ['build', quote(web), '-nologo', '-v', 'q']],
    ["build this version's tool", 'dotnet', ['build', quote(tool(root)), '-nologo', '-v', 'q']],
  ];
  // dotnet ef rolls back (step 3): the tools are restored only when there is a rollback to make
  if (rollsBack) current.push(['restore the dotnet tools', 'dotnet', ['tool', 'restore']]);
  const [baseDone, currentDone] = await Promise.all([chain(base), chain(current)]);
  report(baseDone, base.length);
  report(currentDone, current.length);

  // 2. This version's migrations on a copy: nothing lost, every season replays intact
  const copy = join(work, 'migrated.db');
  for (const suffix of ['', '-wal', '-shm']) {
    if (existsSync(demo + suffix)) cpSync(demo + suffix, copy + suffix);
  }
  const seeded = census(demo);
  step(`the demo database has seasons (${seeded.seasons.length})`, seeded.seasons.length > 0);
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
  const migrated = census(copy);
  step(
    `the copy is at this version's last migration (${lastMigration(root)})`,
    migrated.lastMigration === lastMigration(root),
  );
  nothingLost(seeded, migrated, 'no table lost rows, no season is gone');
  step(
    'every season replays intact with this version',
    run('dotnet', [
      'run',
      '--no-build',
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
  if (!rollsBack) {
    console.log(`No new migrations since ${ref}: nothing to roll back.`);
  } else {
    step(
      `roll back to ${previous}`,
      run('dotnet', [
        'ef',
        'database',
        'update',
        previous,
        // Built with this version above: ef would build it once more
        '--no-build',
        '--project',
        quote(join('src', 'GameEvent.Infrastructure')),
        '--connection',
        quote(database(copy)),
      ]),
    );
    const back = census(copy);
    step(`the copy is at ${previous} again`, back.lastMigration === previous);
    // An empty or partial Down leaves new tables, columns or indexes behind: the schema must be the old one exactly
    const extra = back.schema.filter((line) => !seeded.schema.includes(line));
    const missing = seeded.schema.filter((line) => !back.schema.includes(line));
    for (const line of extra) console.log(`     left over: ${line}`);
    for (const line of missing) console.log(`     missing: ${line}`);
    step("the schema is the previous release's again", extra.length === 0 && missing.length === 0);
    nothingLost(seeded, back, 'the rollback lost no rows and no season');
    // A tool from before J5 has no --all and answers with its usage (exit 2): then the census above is the check
    const older = run(
      'dotnet',
      [
        'run',
        '--no-build',
        '--project',
        quote(tool(baseTree)),
        '--',
        'season-check',
        '--all',
        '--db',
        quote(copy),
      ],
      { capture: true },
    );
    console.log(older.output.trim());
    if (!older.ok && /Usage:/.test(older.output)) {
      console.log(`The tool of ${ref} cannot replay every season: the census stands for it.`);
    } else {
      step('the previous version replays the rolled-back copy intact', older.ok);
    }
  }

  console.log('Migrations are safe to deploy over the previous release.');
} catch (error) {
  if (!(error instanceof Failed)) console.error(error);
  process.exitCode = 1;
} finally {
  cleanUp();
}
