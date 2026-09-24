// Placeholder for project commands that are not implemented yet.
// Usage: node scripts/todo.mjs <command> <task id from docs/PROGRESS.md>
const [command, task] = process.argv.slice(2);
process.stderr.write(
  `"${command}" is not implemented yet: planned in task ${task} (docs/PROGRESS.md).\n`,
);
process.exit(1);
