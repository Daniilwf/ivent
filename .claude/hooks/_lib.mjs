// Общие функции для хуков Claude Code. Работают на Windows, macOS и Linux.
import { existsSync, readFileSync } from 'node:fs';
import { spawnSync } from 'node:child_process';
import { join } from 'node:path';

export async function readInput() {
  let raw = '';
  for await (const chunk of process.stdin) raw += chunk;
  try {
    return raw.trim() ? JSON.parse(raw) : {};
  } catch {
    return {};
  }
}

export function projectRoot(input) {
  return process.env.CLAUDE_PROJECT_DIR || input.cwd || process.cwd();
}

export function npmScripts(root) {
  const pkg = join(root, 'package.json');
  if (!existsSync(pkg)) return {};
  try {
    return JSON.parse(readFileSync(pkg, 'utf8')).scripts ?? {};
  } catch {
    return {};
  }
}

// Запускает команду через оболочку, чтобы работали npm-шимы на Windows.
export function run(command, root) {
  const r = spawnSync(command, { cwd: root, shell: true, encoding: 'utf8', maxBuffer: 32 * 1024 * 1024 });
  return { ok: r.status === 0, output: `${r.stdout ?? ''}${r.stderr ?? ''}` };
}

export function tail(text, max = 6000) {
  return text.length > max ? `…\n${text.slice(-max)}` : text;
}
