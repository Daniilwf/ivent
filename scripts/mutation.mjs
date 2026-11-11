// npm run test:mutation [-- <extra Stryker options, e.g. --concurrency 4 or --mutate "**/Map/*.cs">]
// Stryker.NET over the engine, killed by the engine tests only (D-280). Runs from the engine test project
// (project mode: a solution next to the config would pull in every test project of the solution) with the
// Microsoft Testing Platform runner: xUnit v3 runs tests in its own process, where the VSTest runner never
// activates a mutant. INVARIANT_RUNS=1 keeps the Category=Long theory to one game per property — MTP ignores a
// test-case filter, and the short invariant runs still go in full. Report: test-artifacts/stryker.
import { join } from 'node:path';
import { quote, root, run } from './lib.mjs';

const ok = run(
  'dotnet',
  [
    'tool',
    'run',
    'dotnet-stryker',
    '--config-file',
    quote(join(root, 'stryker-config.json')),
    '--output',
    quote(join(root, 'test-artifacts', 'stryker')),
    ...process.argv.slice(2).map(quote),
  ],
  {
    cwd: join(root, 'src', 'GameEvent.Engine.Tests'),
    env: { INVARIANT_RUNS: '1', INVARIANT_SIZE: '20' },
  },
);
process.exit(ok ? 0 : 1);
