import js from '@eslint/js'
import react from 'eslint-plugin-react'
import reactHooks from 'eslint-plugin-react-hooks'
import tseslint from 'typescript-eslint'

// Inline styles are forbidden: everything visual belongs in kairos.css.
//
// The one sanctioned exception cannot be encoded in config — the rule matches on
// the prop name and cannot see the object's contents, so a style setting only a
// CSS custom property looks identical to one hardcoding a colour. DESIGN.md's one
// authored motion moment needs such a property: the slot list staggers on
// `animation-delay: calc(var(--i) * …)`, so each item carries its own `--i`. At
// that site, and nowhere else:
//
//   // eslint-disable-next-line react/forbid-dom-props -- DESIGN.md stagger: --i is per-item
//   <li style={{ '--i': index } as CSSProperties}>
//
// The disable is per-line and carries its reason. ESLint reports an unused disable
// directive as a warning and `--max-warnings 0` turns that into a failure, so one
// left behind after its markup changes fails the build rather than quietly widening
// the exception.
const inlineStyleMessage =
  'Inline styles are forbidden — style it in kairos.css. The one exception is a CSS custom property the stylesheet consumes, e.g. the DESIGN.md stagger\'s style={{ "--i": i }}; disable this rule on that line with a reason.'

export default tseslint.config(
  js.configs.recommended,
  tseslint.configs.recommended,
  // eslint-plugin-react-hooks 7 ships no config that is only the hook rules:
  // `recommended` and `recommended-latest` both carry the sixteen-odd React
  // Compiler rules — purity, immutability, set-state-in-effect and the rest.
  // Those are opinions this project has not adopted, and one of them rejects
  // `useEffect(() => setState(read()), [])`, which is how the theme toggle will
  // hydrate. So the two rules that catch real bugs are named directly.
  {
    plugins: { 'react-hooks': reactHooks },
    rules: {
      'react-hooks/rules-of-hooks': 'error',
      'react-hooks/exhaustive-deps': 'error',
    },
  },
  {
    files: ['**/*.tsx'],
    plugins: { react },
    rules: {
      'react/forbid-dom-props': [
        'error',
        { forbid: [{ propName: 'style', message: inlineStyleMessage }] },
      ],
    },
  },
)
