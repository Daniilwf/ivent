// npm run test:invariants:long [-- <runs>]
// Long run of random games against the engine invariants (Category=Long tests): every FsCheck property of the
// engine tests is checked again on <runs> games (default 20000, passed as INVARIANT_RUNS). INVARIANT_SIZE (default
// 150) caps the script length. A failure prints the shrunk game as Scenario builder code.
// The Release build (D-670): the same tests and games with the JIT's optimisations on — a Debug build runs the engine and
// the invariant checks without them. Its own output folder, so the Debug build of npm test is not touched.
import { quote, run } from './lib.mjs';

const runs = process.argv[2] ?? process.env.INVARIANT_RUNS ?? '20000';
const ok = run(
  'dotnet',
  [
    'test',
    'src/GameEvent.Engine.Tests',
    '-c',
    'Release',
    '-nologo',
    '--filter',
    quote('Category=Long'),
  ],
  { env: { INVARIANT_RUNS: runs } },
);
process.exit(ok ? 0 : 1);
