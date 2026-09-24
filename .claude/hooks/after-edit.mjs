// PostToolUse (Edit|Write|MultiEdit): форматирует и линтит изменённый файл
// через `npm run -s format:file -- <путь>`, если такой скрипт есть в корневом package.json.
// При ошибке выходит с кодом 2: Claude видит проблему и исправляет её.
import { readInput, projectRoot, npmScripts, run, tail } from './_lib.mjs';

const input = await readInput();
const root = projectRoot(input);
const file = input.tool_input?.file_path;

if (!file || !npmScripts(root)['format:file']) process.exit(0);

const { ok, output } = run(`npm run -s format:file -- "${file}"`, root);
if (!ok) {
  process.stderr.write(`Форматтер или линтер нашёл проблемы в ${file}. Исправь их:\n${tail(output, 4000)}\n`);
  process.exit(2);
}
process.exit(0);
