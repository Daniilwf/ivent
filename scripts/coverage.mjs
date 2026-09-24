// npm run coverage
// .NET coverage (coverlet) for fast and slow tests, merged into an HTML and text report.
import { readFileSync, rmSync } from 'node:fs';
import { join } from 'node:path';
import { quote, root, run } from './lib.mjs';

const out = join(root, 'test-artifacts', 'coverage');
rmSync(out, { recursive: true, force: true });

const tested = run('dotnet', [
  'test',
  'GameEvent.slnx',
  '-nologo',
  '--filter',
  quote('Category!=Long'),
  '--collect',
  quote('XPlat Code Coverage'),
  '--results-directory',
  quote(join(out, 'raw')),
]);
if (!tested) process.exit(1);

const reported = run('dotnet', [
  'tool',
  'run',
  'reportgenerator',
  quote(`-reports:${join(out, 'raw', '**', 'coverage.cobertura.xml')}`),
  quote(`-targetdir:${join(out, 'report')}`),
  '-reporttypes:Html;TextSummary;Cobertura',
  quote('-assemblyfilters:+GameEvent.*;-GameEvent.*.Tests'),
]);
if (!reported) process.exit(1);

process.stdout.write(readFileSync(join(out, 'report', 'Summary.txt'), 'utf8'));
