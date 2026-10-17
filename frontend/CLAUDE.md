# Frontend rules

Loaded on top of the repository root `AGENTS.md`, which holds the eight rules that bind every change and is not repeated here. These three bind TypeScript and CSS specifically. Anything visual is also governed by `../docs/ui/DESIGN.md`.

**8. Weight is correctness — the frontend half.** The booking route has a bundle budget the build enforces through `scripts/bundle-budget.sh` — the invitee is on a mid-range Android over mobile data, and being lighter than the alternative is the product's whole argument. Headroom is measured, not estimated: `scripts/bundle-budget.sh` holds the ceiling, its output is the current figure, and a pull request that moves either quotes both. The backend half of this rule is in `backend/CLAUDE.md`.

**9. Each side knows nothing of the other's internals — the frontend half.** The only contract is HTTP. No frontend code reaches into backend internals and no frontend assertion depends on how a handler is built; equally, frontend rules are enforced by frontend tooling rather than by a .NET test.

**17. No new CSS** unless nothing in `kairos.css` expresses the screen. Both exceptions granted so far were deliberate and named in the report that granted them; do the same.

## The accessibility floor

No formal compliance target, but the public page cannot know who opens it: keyboard reachable throughout, visible focus, readable contrast, 44px minimum touch targets, `prefers-reduced-motion` honoured. The SPA authors the focus management and announcement a server-rendered page got for free.

## Copy a visitor reads is never hard-wrapped

A sentence rendered on screen lives on one source line, however long. Not a numbered rule — a convention, and it binds the copy only. Comments stay wrapped the way the rest of this tree wraps them.

The reason is the same one the vault gives for markdown: a break at column 90 lies about where the sentence ends, makes a one-word edit a three-line diff, and hides the sentence from `grep`. Searching for the copy an operator quoted back at you should find it.

**Joining is not always free.** JSX turns a newline *between two text lines* into one space, and removes one *next to a line holding only an expression*. So `…minutes.` above `{count > 1 ? ' A shorter meeting may fit.' : ''}` renders with the single space that string already carries — join them and it renders with two. Leave a paragraph wrapped when any of its lines is a bare `{expression}`, and say so where it is not obvious.
