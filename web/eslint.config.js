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
      // Every fold opens with the Summary primitive and its chevron: the browser's triangle is hidden (K2-5)
      'no-restricted-syntax': [
        'error',
        {
          selector: "JSXOpeningElement[name.name='summary']",
          message: 'A fold opens with <Summary> from src/ui/Disclosure.tsx (K2-5).',
        },
      ],
      // The animation engine stays out of the main page's script (FL-3, DESIGN.md «Производительность»): `m` inside
      // <Motion>, `animate` from loadMotion(); only src/board/motionFeatures.ts takes the heavy parts
      '@typescript-eslint/no-restricted-imports': [
        'error',
        {
          paths: [
            {
              name: 'motion/react',
              importNames: ['motion', 'animate', 'domAnimation', 'domMax'],
              allowTypeImports: true,
              message: 'Use `m` inside <Motion> and `animate` from loadMotion() (src/board, FL-3).',
            },
            { name: 'motion', allowTypeImports: true, message: 'Import from motion/react (FL-3).' },
            {
              name: 'framer-motion',
              allowTypeImports: true,
              message: 'Import from motion/react (FL-3).',
            },
          ],
        },
      ],
    },
  },
  {
    files: ['src/board/motionFeatures.ts'],
    rules: { '@typescript-eslint/no-restricted-imports': 'off' },
  },
  {
    files: ['src/ui/Disclosure.tsx'],
    rules: { 'no-restricted-syntax': 'off' },
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
