// Release notes for «Что нового» (J4, D-201): the player-facing lines of the commits since the previous release.
// A commit that changes something players notice carries a trailer in Russian, for the players:
//   Release-note: Колесо показывает, кто уже прошёл выпавшую игру
// Usage: npm run release:notes -- <version>   (e.g. v1.2.0; run before tagging the release, commit the result)
import { spawnSync } from 'node:child_process';
import { readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { fail, root } from './lib.mjs';

// git is an executable: no shell, so the % of the log format reaches it as it is (cmd would expand it)
function git(...args) {
  const result = spawnSync('git', args, {
    cwd: root,
    encoding: 'utf8',
    maxBuffer: 64 * 1024 * 1024,
  });
  return { ok: result.status === 0, output: `${result.stdout}${result.stderr}` };
}

const version = process.argv[2] ?? fail('Usage: npm run release:notes -- <version>');
if (!/^v\d+\.\d+\.\d+$/.test(version)) fail(`A version looks like v1.2.0, not ${version}.`);

const file = join(root, 'web', 'src', 'whatsNew', 'changelog.json');
const changelog = JSON.parse(readFileSync(file, 'utf8'));
if (changelog.some((entry) => entry.version === version))
  fail(`${version} is in the changelog already.`);

const previous = git('describe', '--tags', '--abbrev=0', '--match', 'v*');
const range = previous.ok ? `${previous.output.trim()}..HEAD` : 'HEAD';
const log = git('log', '--format=%(trailers:key=Release-note,valueonly,separator=%x1f)%x1e', range);
if (!log.ok) fail(log.output);

const items = [
  ...new Set(
    log.output
      .split('\x1e')
      .reverse() // oldest commit first: the story of the release in order
      .flatMap((commit) => commit.split('\x1f'))
      .map((line) => line.trim())
      .filter(Boolean),
  ),
];

const today = new Date().toISOString().slice(0, 10);
changelog.unshift({ version, date: today, items });
writeFileSync(file, `${JSON.stringify(changelog, null, 2)}\n`);
console.log(
  `${version}: ${items.length} note(s) since ${previous.ok ? previous.output.trim() : 'the start'}.`,
);
