// Shared helpers for project scripts. Cross-platform: Windows (cmd shims) and Linux CI.
import { spawnSync } from 'node:child_process';
import { dirname, join, relative, sep } from 'node:path';
import { fileURLToPath } from 'node:url';

export const root = join(dirname(fileURLToPath(import.meta.url)), '..');

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

/**
 * Quotes an argument for the shell used by run() on Windows. cmd.exe expands %VAR% and !VAR!
 * even inside quotes, so such arguments are refused rather than passed on.
 */
export function quote(arg) {
  if (process.platform === 'win32' && /[%!"]/.test(arg)) {
    throw new Error(`Refusing a shell argument with %, ! or ": ${arg}`);
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
