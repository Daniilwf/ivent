import js from '@eslint/js';
import prettier from 'eslint-config-prettier';
import reactHooks from 'eslint-plugin-react-hooks';
import reactRefresh from 'eslint-plugin-react-refresh';
import globals from 'globals';
import design from './eslint-rules/design-tokens.js';
import tseslint from 'typescript-eslint';

export default tseslint.config(
  { ignores: ['dist', 'coverage', 'src/api/schema.ts'] },
  {
    files: ['**/*.{ts,tsx}'],
    extends: [js.configs.recommended, ...tseslint.configs.strictTypeChecked, prettier],
    languageOptions: {
      ecmaVersion: 2023,
      globals: globals.browser,
      parserOptions: {
        projectService: true,
        tsconfigRootDir: import.meta.dirname,
      },
    },
    plugins: {
      'react-hooks': reactHooks,
      'react-refresh': reactRefresh,
      design,
    },
    rules: {
      ...reactHooks.configs.recommended.rules,
      'react-refresh/only-export-components': ['error', { allowConstantExport: true }],
      '@typescript-eslint/restrict-template-expressions': ['error', { allowNumber: true }],
      'design/tokens-only': 'error',
    },
  },
  // The tokens themselves live in src/design/: the one place allowed to hold raw values
  {
    files: ['src/design/**/*.{ts,tsx}'],
    rules: { 'design/tokens-only': 'off' },
  },
  {
    files: ['*.js', 'eslint-rules/**/*.js'],
    extends: [js.configs.recommended, prettier],
    languageOptions: { globals: globals.node },
  },
);
