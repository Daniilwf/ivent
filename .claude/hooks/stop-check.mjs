// Stop: не даёт агенту закончить ход, если в изменённом коде красная сборка, линтер или быстрые тесты.
// Логика проверки живёт в `npm run -s check:stop` корневого package.json; хук только решает, когда её звать.
//
// Проверка пропускается:
//  - если изменились только документы (*.md, docs/, .claude/);
//  - в режиме планирования;
//  - один раз, если существует .claude/state/allow-stop-once (агент ждёт ответа посреди работы и объяснил почему);
//  - после 3 неудачных попыток подряд, чтобы не зациклиться (тогда агент обязан сообщить о красной сборке).
import { existsSync, mkdirSync, readFileSync, unlinkSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { readInput, projectRoot, npmScripts, run, tail } from './_lib.mjs';

const MAX_RETRIES = 3;
const input = await readInput();
const root = projectRoot(input);
const stateDir = join(root, '.claude', 'state');
const allowOnce = join(stateDir, 'allow-stop-once');
const retriesFile = join(stateDir, 'stop-retries');

if (input.permission_mode === 'plan') process.exit(0);

if (existsSync(allowOnce)) {
  unlinkSync(allowOnce);
  process.exit(0);
}

const status = run('git -c core.quotepath=off status --porcelain', root);
if (!status.ok) process.exit(0);

const changedCode = status.output
  .split('\n')
  .map((line) => line.slice(3).trim())
  .map((path) => (path.includes(' -> ') ? path.split(' -> ')[1] : path))
  .filter(Boolean)
  .filter((path) => !path.endsWith('.md') && !path.startsWith('docs/') && !path.startsWith('.claude/'));

if (changedCode.length === 0 || !npmScripts(root)['check:stop']) process.exit(0);

const retries = existsSync(retriesFile) ? Number(readFileSync(retriesFile, 'utf8')) || 0 : 0;
const { ok, output } = run('npm run -s check:stop', root);

mkdirSync(stateDir, { recursive: true });
if (ok) {
  if (existsSync(retriesFile)) unlinkSync(retriesFile);
  process.exit(0);
}

if (retries + 1 >= MAX_RETRIES) {
  if (existsSync(retriesFile)) unlinkSync(retriesFile);
  process.stdout.write(JSON.stringify({
    systemMessage: 'Сборка или тесты всё ещё красные после нескольких попыток. Агент должен явно сообщить об этом.',
  }));
  process.exit(0);
}

writeFileSync(retriesFile, String(retries + 1));
process.stderr.write(
  'Сборка, линтер или быстрые тесты красные. Исправь, прежде чем заканчивать ход.\n' +
  'Если ты посреди работы и ждёшь моего ответа, создай файл .claude/state/allow-stop-once и в ответе объясни, что осталось красным и почему.\n\n' +
  tail(output),
);
process.exit(2);
