# Frontend rules

Loaded on top of the repository root `AGENTS.md`, which holds the eight rules that bind every change and is not repeated here. These three bind TypeScript and CSS specifically. Anything visual is also governed by `../docs/ui/DESIGN.md`.

**8. Weight is correctness — the frontend half.** The booking route has a bundle budget the build enforces through `scripts/bundle-budget.sh` — the invitee is on a mid-range Android over mobile data, and being lighter than the alternative is the product's whole argument. Headroom is measured, not estimated: `docs/measurements.md` carries the current figure and the per-React-version ceiling it is measured against. The backend half of this rule is in `backend/CLAUDE.md`.

**9. Each side knows nothing of the other's internals — the frontend half.** The only contract is HTTP. No frontend code reaches into backend internals and no frontend assertion depends on how a handler is built; equally, frontend rules are enforced by frontend tooling rather than by a .NET test.

**17. No new CSS** unless nothing in `kairos.css` expresses the screen. Both exceptions granted so far were deliberate and named in the report that granted them; do the same.

## The accessibility floor

No formal compliance target, but the public page cannot know who opens it: keyboard reachable throughout, visible focus, readable contrast, 44px minimum touch targets, `prefers-reduced-motion` honoured. The SPA authors the focus management and announcement a server-rendered page got for free.
