// npm run test:invariants:long [-- <runs>]
// Long run of random games against the engine invariants (Category=Long tests): every FsCheck property of the
// engine tests is checked again on <runs> games (default 20000, passed as INVARIANT_RUNS). INVARIANT_SIZE (default
// 150) caps the script length. A failure prints the shrunk game as Scenario builder code.
import { quote, run } from './lib.mjs';

const runs = process.argv[2] ?? process.env.INVARIANT_RUNS ?? '20000';
const ok = run(
  'dotnet',
  ['test', 'src/GameEvent.Engine.Tests', '-nologo', '--filter', quote('Category=Long')],
  { env: { INVARIANT_RUNS: runs } },
);
process.exit(ok ? 0 : 1);
