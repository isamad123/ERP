// Lint rules of spec 13 section 24 (slice S1.12a, Build item 1): TypeScript strict rules, React hooks, accessibility
// (jsx-a11y), no literal user-facing text, and no UI library outside the approved list (CLAUDE.md front-end rules).
import js from '@eslint/js';
import tseslint from 'typescript-eslint';
import jsxA11y from 'eslint-plugin-jsx-a11y';
import reactHooks from 'eslint-plugin-react-hooks';
import globals from 'globals';
import local from './tools/eslint-rules/index.js';

export default tseslint.config(
  { ignores: ['**/dist/**', '**/dist-e2e/**', '**/node_modules/**', 'generated/**', '**/src/generated/**', '**/test-results/**', '**/playwright-report/**'] },
  js.configs.recommended,
  ...tseslint.configs.strict,
  {
    files: ['**/*.{ts,tsx}'],
    languageOptions: { globals: { ...globals.browser } },
    plugins: { 'jsx-a11y': jsxA11y, 'react-hooks': reactHooks, local },
    rules: {
      ...jsxA11y.flatConfigs.strict.rules,
      'react-hooks/rules-of-hooks': 'error',
      'react-hooks/exhaustive-deps': 'error',
      'local/no-literal-text': 'error',
      '@typescript-eslint/consistent-type-imports': 'error',
      'no-restricted-imports': ['error', {
        patterns: [
          { group: ['@radix-ui/*', '@mui/*', 'antd', '@chakra-ui/*', 'bootstrap', 'styled-components', '@emotion/*', 'i18next', 'react-i18next'], message: 'Only the approved UI libraries may be used (ADR web stack, D-09).' },
        ],
      }],
      'no-restricted-syntax': ['error',
        { selector: "MemberExpression[property.name='localStorage']", message: 'Browser storage may not hold personal data or tokens (S1.12 Q3, Q13); use the preferences helper.' },
        { selector: "MemberExpression[object.name='localStorage']", message: 'Browser storage may not hold personal data or tokens (S1.12 Q3, Q13); use the preferences helper.' },
        { selector: "MemberExpression[object.name='sessionStorage']", message: 'Browser storage may not hold personal data or tokens (S1.12 Q3, Q13).' },
        { selector: "MemberExpression[property.name='innerHTML']", message: 'Never write HTML strings into the page.' },
        { selector: "CallExpression[callee.object.name='Date'][callee.property.name='now']", message: 'Use the injected clock (src/session/clock.ts).' },
      ],
    },
  },
  {
    // Tests, scripts and tools may hold literal text and use Node globals.
    files: ['**/*.test.{ts,tsx}', '**/e2e/**', '**/scripts/**', 'tools/**', '*.config.{js,ts}', 'apps/*/*.config.ts', '**/catalogue/**'],
    languageOptions: { globals: { ...globals.node, ...globals.browser } },
    rules: { 'local/no-literal-text': 'off', 'no-restricted-syntax': 'off', '@typescript-eslint/no-non-null-assertion': 'off' },
  },
  {
    files: ['**/*.{js,mjs}'],
    languageOptions: { globals: { ...globals.node } },
  },
  {
    files: ['apps/*/public/**/*.js'],
    languageOptions: { globals: { ...globals.browser } },
  },
);
