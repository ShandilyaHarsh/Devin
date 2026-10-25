import js from '@eslint/js';
import react from 'eslint-plugin-react';
import reactHooks from 'eslint-plugin-react-hooks';
import globals from 'globals';
import tseslint from 'typescript-eslint';

export default tseslint.config(
  {
    ignores: ['**/node_modules/**', '**/dist/**', '**/wwwroot/**', '**/bin/**', '**/obj/**', '**/coverage/**'],
  },
  {
    files: ['platform/web/**/*.{ts,tsx}', 'apps/*/web/**/*.{ts,tsx}', 'tests/web/**/*.{ts,tsx}', '*.{js,ts}'],
    extends: [js.configs.recommended, ...tseslint.configs.recommended],
    languageOptions: {
      ecmaVersion: 2023,
      sourceType: 'module',
      globals: { ...globals.browser, ...globals.node },
    },
    plugins: { react, 'react-hooks': reactHooks },
    settings: { react: { version: 'detect' } },
    rules: {
      ...react.configs.recommended.rules,
      ...react.configs['jsx-runtime'].rules,
      ...reactHooks.configs.recommended.rules,
      'react/no-danger': 'error',
      'react/prop-types': 'off',
      'no-eval': 'error',
      'no-implied-eval': 'error',
      'no-new-func': 'error',
      'no-restricted-globals': [
        'error',
        { name: 'localStorage', message: 'No tokens or PII in browser storage.' },
        { name: 'sessionStorage', message: 'No tokens or PII in browser storage.' },
      ],
      'no-restricted-properties': [
        'error',
        { object: 'window', property: 'localStorage', message: 'No tokens or PII in browser storage.' },
        { object: 'window', property: 'sessionStorage', message: 'No tokens or PII in browser storage.' },
      ],
    },
  },
);
