// npm run gen:api
// Writes the OpenAPI document of the backend and the TypeScript client types generated from it.
// API types on the frontend are never written by hand; CI fails if these files are stale.
// Both files are generated as is (not formatted), so a fresh run on any OS gives the same bytes.
import { readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { quote, root, run } from './lib.mjs';

const apiDir = join(root, 'web', 'src', 'api');
const steps = [
  ['dotnet', ['build', 'src/GameEvent.Web', '-nologo', '-v', 'q', '-p:GenerateApiDocs=true']],
  [
    'node',
    [
      quote(join(root, 'node_modules', 'openapi-typescript', 'bin', 'cli.js')),
      quote(join(apiDir, 'openapi.json')),
      '--output',
      quote(join(apiDir, 'schema.ts')),
      '--enum-values',
    ],
  ],
];

const [build, generate] = steps;
if (!run(...build)) process.exit(1);

// XML doc comments carry the OS line ending (an escaped CR LF on Windows): normalize so any OS writes
// the same bytes.
const document = join(apiDir, 'openapi.json');
writeFileSync(document, readFileSync(document, 'utf8').replaceAll('\\r\\n', '\\n'));

if (!run(...generate)) process.exit(1);
