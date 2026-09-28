// Shared helpers for project scripts. Cross-platform: Windows (cmd shims) and Linux CI.
import { spawn, spawnSync } from 'node:child_process';
import { dirname, join, relative, sep } from 'node:path';
import { fileURLToPath } from 'node:url';

// Upper-case drive letter: a hook may start us from "c:\..." and Vitest then loads two copies of
// the same module under differently-cased paths (jest-dom matchers land on the wrong `expect`).
export const root = join(dirname(fileURLToPath(import.meta.url)), '..').replace(/^[a-z]:/, (d) =>
  d.toUpperCase(),
);

/** Runs a command, streaming output. Returns true on exit code 0. */
export function run(command, args = [], options = {}) {
  const result = spawnSync(command, args, {
    cwd: options.cwd ?? root,
    stdio: options.capture ? 'pipe' : 'inherit',
    encoding: 'utf8',
    env: { ...process.env, ...options.env },
    // npm/npx are .cmd shims on Windows and need a shell.
    shell: process.platform === 'win32',
    maxBuffer: 64 * 1024 * 1024,
  });
  if (options.capture)
    return { ok: result.status === 0, output: `${result.stdout}${result.stderr}` };
  return result.status === 0;
}

/** Like run() with capture, but without blocking: resolves to { ok, output } when the command exits. */
export function runAsync(command, args = [], options = {}) {
  return new Promise((resolve) => {
    const child = spawn(command, args, {
      cwd: options.cwd ?? root,
      stdio: ['ignore', 'pipe', 'pipe'],
      env: { ...process.env, ...options.env },
      shell: process.platform === 'win32',
    });
    let output = '';
    child.stdout.setEncoding('utf8').on('data', (chunk) => (output += chunk));
    child.stderr.setEncoding('utf8').on('data', (chunk) => (output += chunk));
    child.on('error', (error) => resolve({ ok: false, output: `${output}${error.message}` }));
    child.on('close', (code) => resolve({ ok: code === 0, output }));
  });
}

/**
 * Quotes an argument for the shell used by run() on Windows. cmd.exe expands %VAR% even inside quotes,
 * so such arguments are refused (delayed !VAR! expansion is off in `cmd /c`, so ! is allowed).
 */
export function quote(arg) {
  if (process.platform === 'win32' && /[%"]/.test(arg)) {
    throw new Error(`Refusing a shell argument with % or ": ${arg}`);
  }
  return process.platform === 'win32' && /[\s&()^|<>]/.test(arg) ? `"${arg}"` : arg;
}

/** Path relative to the repository root with forward slashes. */
export function repoPath(file) {
  return relative(root, file).split(sep).join('/');
}

export function fail(message) {
  process.stderr.write(`${message}\n`);
  process.exit(1);
}
