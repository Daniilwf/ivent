import { createServer, request, type IncomingHttpHeaders } from 'node:http';
import { connect, type AddressInfo } from 'node:net';
import { constants, createGzip } from 'node:zlib';

// The live site stands behind Caddy, which compresses every answer (deploy/Caddyfile: `encode zstd gzip`); the E2E site
// is Kestrel alone and sends the scripts as they are. A page's speed over a slow network is measured through this
// proxy, which gzips like Caddy, so the number is the one a player would get (D-233). WebSockets pass through as is.

const compressible = /^(text\/|application\/(javascript|json|problem\+json|xml)|image\/svg\+xml)/;

export async function compressingProxy(
  target: string,
): Promise<{ url: string; close: () => Promise<void> }> {
  const upstream = new URL(target);
  const server = createServer((incoming, outgoing) => {
    const forward = request(
      {
        host: upstream.hostname,
        port: upstream.port,
        method: incoming.method,
        path: incoming.url,
        headers: { ...incoming.headers, host: upstream.host, 'accept-encoding': 'identity' },
      },
      (answer) => {
        answer.on('error', () => outgoing.destroy());
        const headers: IncomingHttpHeaders = { ...answer.headers };
        const type = headers['content-type'] ?? '';
        const gzip =
          /\bgzip\b/.test(incoming.headers['accept-encoding'] ?? '') &&
          compressible.test(type) &&
          !headers['content-encoding'];
        if (!gzip) {
          outgoing.writeHead(answer.statusCode ?? 502, headers);
          answer.pipe(outgoing);
          return;
        }
        delete headers['content-length'];
        headers['content-encoding'] = 'gzip';
        headers['vary'] = 'Accept-Encoding';
        outgoing.writeHead(answer.statusCode ?? 502, headers);
        // Flushed per chunk: a stream (server-sent events) is not held back
        answer.pipe(createGzip({ flush: constants.Z_SYNC_FLUSH })).pipe(outgoing);
      },
    );
    // A connection reset on either side (the page closed mid-answer) ends the other quietly
    forward.on('error', () => outgoing.destroy());
    incoming.on('error', () => forward.destroy());
    outgoing.on('error', () => forward.destroy());
    incoming.pipe(forward);
  });
  server.on('upgrade', (incoming, socket, head) => {
    const back = connect(Number(upstream.port), upstream.hostname, () => {
      const lines = [`${incoming.method ?? 'GET'} ${incoming.url ?? '/'} HTTP/1.1`];
      for (let i = 0; i < incoming.rawHeaders.length; i += 2)
        lines.push(
          `${incoming.rawHeaders[i] ?? ''}: ${
            incoming.rawHeaders[i]?.toLowerCase() === 'host'
              ? upstream.host
              : (incoming.rawHeaders[i + 1] ?? '')
          }`,
        );
      back.write(`${lines.join('\r\n')}\r\n\r\n`);
      back.write(head);
      back.pipe(socket).pipe(back);
    });
    back.on('error', () => socket.destroy());
    socket.on('error', () => back.destroy());
  });
  await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve));
  const { port } = server.address() as AddressInfo;
  return {
    url: `http://localhost:${String(port)}`,
    close: () =>
      new Promise((resolve) => {
        server.closeAllConnections();
        server.close(() => {
          resolve();
        });
      }),
  };
}
