/** Joins class names, skipping the empty ones. The design linter checks its arguments like className. */
export function cx(...parts: (string | false | null | undefined)[]) {
  return parts.filter(Boolean).join(' ');
}
