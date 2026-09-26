// A static server for a built frontend, with no dependencies: the visual tests serve web/dist with it inside the
// Playwright container. Unknown paths get index.html, like the site's own fallback (/styleguide).
//   node scripts/static-server.mjs <dir> <port>
import { createReadStream, existsSync, statSync } from 'node:fs';
import { createServer } from 'node:http';
import { extname, join, relative, resolve, isAbsolute } from 'node:path';

const [dir = 'web/dist', port = '4180'] = process.argv.slice(2);
const root = resolve(dir);
const types = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.svg': 'image/svg+xml',
  '.png': 'image/png',
  '.jpg': 'image/jpeg',
  '.gif': 'image/gif',
  '.woff2': 'font/woff2',
  '.woff': 'font/woff',
  '.json': 'application/json',
};

/** The file for a request path, or null when the path leaves the root */
function fileFor(path) {
  const file = resolve(root, `.${path}`);
  const inside = relative(root, file);
  if (inside.startsWith('..') || isAbsolute(inside)) return null;
  return existsSync(file) && !statSync(file).isDirectory() ? file : join(root, 'index.html');
}

createServer((request, response) => {
  let path;
  try {
    path = decodeURIComponent(new URL(request.url ?? '/', 'http://localhost').pathname);
  } catch {
    response.writeHead(400).end();
    return;
  }
  const file = fileFor(path);
  if (!file) {
    response.writeHead(404).end();
    return;
  }
  response.writeHead(200, { 'content-type': types[extname(file)] ?? 'application/octet-stream' });
  createReadStream(file).pipe(response);
}).listen(Number(port), '127.0.0.1', () => {
  process.stdout.write(`Serving ${root} on 127.0.0.1:${port}\n`);
});
