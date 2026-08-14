import js from '@eslint/js'
import react from 'eslint-plugin-react'
import reactHooks from 'eslint-plugin-react-hooks'
import tseslint from 'typescript-eslint'

export default tseslint.config(
  { ignores: ['dist'] },
  js.configs.recommended,
  tseslint.configs.recommended,
  // The Vite template lints hooks by default, through oxlint. This project runs
  // ESLint instead, so the rules-of-hooks check has to be carried over rather
  // than lost in the swap.
  reactHooks.configs.flat['recommended-latest'],
  {
    files: ['**/*.tsx'],
    plugins: { react },
    // Inline styles are caught here rather than by a test, so a violation
    // surfaces while typing. Everything visual belongs in kairos.css.
    rules: { 'react/forbid-dom-props': ['error', { forbid: ['style'] }] },
  },
)
