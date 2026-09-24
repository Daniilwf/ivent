// npm run gen:api
// Writes the OpenAPI document of the backend and the TypeScript client types generated from it.
// API types on the frontend are never written by hand; CI fails if these files are stale.
// Both files are generated as is (not formatted), so a fresh run on any OS gives the same bytes.
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
      quote(join(apiDir, 'schema.d.ts')),
      '--enum-values',
    ],
  ],
];

for (const [command, args] of steps) {
  if (!run(command, args)) process.exit(1);
}
