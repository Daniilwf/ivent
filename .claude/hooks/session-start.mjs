// SessionStart: в начале сессии подкладывает агенту сводку — ветку, незакоммиченные изменения,
// раздел «Сейчас» из docs/PROGRESS.md и число открытых багов из docs/BUGS.md.
import { existsSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { readInput, projectRoot, run } from './_lib.mjs';

const input = await readInput();
const root = projectRoot(input);
const lines = ['Сводка проекта на старте сессии (из хука SessionStart):'];

const branch = run('git branch --show-current', root);
if (branch.ok) lines.push(`- Ветка: ${branch.output.trim() || 'нет'}`);

const status = run('git -c core.quotepath=off status --porcelain', root);
if (status.ok) {
  const count = status.output.split('\n').filter(Boolean).length;
  lines.push(`- Незакоммиченных файлов: ${count}`);
}

const last = run('git log -1 --pretty=format:"%h %s"', root);
if (last.ok && last.output.trim()) lines.push(`- Последний коммит: ${last.output.trim()}`);

const progress = join(root, 'docs', 'PROGRESS.md');
if (existsSync(progress)) {
  const text = readFileSync(progress, 'utf8');
  const match = text.match(/## Сейчас\n([\s\S]*?)(\n## |$)/);
  const section = (match ? match[1] : text.split('\n').slice(0, 40).join('\n')).trim();
  lines.push('', 'Из docs/PROGRESS.md:', section.split('\n').slice(0, 40).join('\n'));
} else {
  lines.push('- docs/PROGRESS.md ещё нет.');
}

const bugs = join(root, 'docs', 'BUGS.md');
if (existsSync(bugs)) {
  const open = (readFileSync(bugs, 'utf8').match(/^- \[ \]/gm) || []).length;
  lines.push('', `Открытых багов в docs/BUGS.md: ${open}`);
}

lines.push('', 'Перед изменением кода прочитай docs/PROGRESS.md, docs/DECISIONS.md и открытые пункты docs/BUGS.md целиком.');
process.stdout.write(lines.join('\n'));
