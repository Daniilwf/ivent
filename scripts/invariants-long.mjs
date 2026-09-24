// npm run test:invariants:long [-- <runs>]
// Long run of random games against the engine invariants (Category=Long tests).
// The number of games is passed to FsCheck through INVARIANT_RUNS.
import { quote, run } from './lib.mjs';

const runs = process.argv[2] ?? '20000';
const ok = run(
  'dotnet',
  ['test', 'src/GameEvent.Engine.Tests', '-nologo', '--filter', quote('Category=Long')],
  { env: { INVARIANT_RUNS: runs } },
);
process.exit(ok ? 0 : 1);
