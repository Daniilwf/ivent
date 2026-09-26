import { cells, players, type PreviewPlayer } from './data';

/** Cell n (1…60) of a serpentine board with this many columns: row and column, the path turning at each row's end. */
export function place(n: number, columns: number) {
  const row = Math.floor((n - 1) / columns);
  const offset = (n - 1) % columns;
  return { row, column: row % 2 === 0 ? offset : columns - 1 - offset };
}

export function playersOn(n: number): PreviewPlayer[] {
  return players.filter((p) => p.cell === n);
}

/** The board's cells in reading order of the grid, each with its number: a serpentine of 60. */
export function boardCells(columns: number) {
  return Array.from({ length: cells }, (_, i) => i + 1)
    .map((n) => ({ n, ...place(n, columns) }))
    .sort((a, b) => a.row - b.row || a.column - b.column);
}
