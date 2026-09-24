// Conventional Commits: feat:, fix:, chore:, docs:, test:, refactor:, ci:, build:, perf:
export default {
  extends: ['@commitlint/config-conventional'],
  rules: {
    // Bodies may contain long links and Co-Authored-By trailers.
    'body-max-line-length': [0],
    'footer-max-line-length': [0],
  },
};
