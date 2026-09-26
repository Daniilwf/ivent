// npm run clean [-- --dry-run]
// Removes what the agents and the tests leave behind and nobody needs any more:
//   - git worktrees (the agents' copies of the repo) whose branch a pull request merged into origin/main; one with
//     uncommitted changes, or one just created for a task that has not started, is kept and named;
//   - local branches a pull request merged into origin/main (never main or the current one);
//   - test artifacts (screenshots, traces, coverage) and throwaway databases (e2e, screenshots, the migration check).
// Kept: the development database var/dev.db and the demo one var/demo.db, and every unmerged branch and its worktree.
import { spawnSync } from 'node:child_process';
import { existsSync, readdirSync, rmSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { root } from './lib.mjs';

const dryRun = process.argv.includes('--dry-run');
const said = (what) => console.log(`${dryRun ? 'would remove' : 'removed'} ${what}`);

// git runs without a shell: paths with spaces and refs reach it as they are
function git(...args) {
  const result = spawnSync('git', args, { cwd: root, encoding: 'utf8' });
  return {
    ok: result.status === 0,
    out: (result.stdout ?? '').trim(),
    err: (result.stderr ?? '').trim(),
  };
}

git('fetch', '--quiet', '--prune', 'origin');
const main = git('rev-parse', 'origin/main').out;
// Merged means brought in by a merge (a pull request). A commit on main's own first-parent line is where a branch was
// just created: a task that has not started yet looks «merged» that way and must stay
const mainLine = new Set(git('rev-list', '--first-parent', main).out.split('\n'));
const merged = (commit) =>
  !mainLine.has(commit) && git('merge-base', '--is-ancestor', commit, main).ok;

// 1. Worktrees
git('worktree', 'prune');
const here = resolve(root).toLowerCase();
const blocks = git('worktree', 'list', '--porcelain').out.split(/\n\n+/);
for (const block of blocks) {
  const path = /^worktree (.+)$/m.exec(block)?.[1];
  const head = /^HEAD (\w+)$/m.exec(block)?.[1];
  const branch = /^branch refs\/heads\/(.+)$/m.exec(block)?.[1];
  if (!path || !head || resolve(path).toLowerCase() === here) continue;
  if (!merged(head)) {
    console.log(`kept worktree ${path} (${branch ?? 'detached'}: not in main yet)`);
    continue;
  }
  const dirty = spawnSync('git', ['status', '--porcelain'], { cwd: path, encoding: 'utf8' });
  if (dirty.status === 0 && dirty.stdout.trim() !== '') {
    console.log(`kept worktree ${path}: it has uncommitted changes`);
    continue;
  }
  if (!dryRun) {
    // Twice --force: a worktree an interrupted agent left locked is removed too
    const removed = git('worktree', 'remove', '--force', '--force', path);
    if (!removed.ok) {
      console.log(`could not remove worktree ${path}: ${removed.err}`);
      continue;
    }
  }
  said(`worktree ${path}`);
}

// 2. Local branches already in main
const current = git('branch', '--show-current').out;
const branches = git('branch', '--format=%(refname:short)', '--merged', main)
  .out.split('\n')
  .filter(Boolean);
for (const branch of branches) {
  if (branch === 'main' || branch === current || !merged(git('rev-parse', branch).out)) continue;
  if (!dryRun && !git('branch', '-D', branch).ok) continue; // still checked out in a kept worktree
  said(`branch ${branch}`);
}

// 3. Throwaway files
const throwaway = [
  'test-artifacts',
  join('var', 'migration-check'),
  ...['e2e.db', 'shots.db', 'design-time.db'].flatMap((db) =>
    [db, `${db}-wal`, `${db}-shm`].map((f) => join('var', f)),
  ),
  'design-time.db',
];
for (const entry of throwaway) {
  const path = join(root, entry);
  if (!existsSync(path)) continue;
  if (!dryRun) rmSync(path, { recursive: true, force: true });
  said(entry);
}
for (const log of existsSync(join(root, 'var')) ? readdirSync(join(root, 'var')) : []) {
  if (!log.endsWith('.log')) continue;
  if (!dryRun) rmSync(join(root, 'var', log), { force: true });
  said(join('var', log));
}

console.log(dryRun ? 'Dry run: nothing was removed.' : 'Clean.');
