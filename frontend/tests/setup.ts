import { JSDOM } from 'jsdom'

// Node 26 declares a global `localStorage` that reads as undefined unless the
// process was started with --localstorage-file. Vitest's jsdom environment only
// copies window properties globalThis does not already have, so that stub keeps
// its place and jsdom's real Storage never lands. Put a real one there.
const { window } = new JSDOM('', { url: 'http://localhost:3000/' })

Object.defineProperty(globalThis, 'localStorage', {
  value: window.localStorage,
  configurable: true,
})

// jsdom ships no clipboard. The editor's Copy button needs one to exist; what
// it writes is asserted where it matters and stubbed everywhere else.
if (navigator.clipboard === undefined) {
  Object.defineProperty(navigator, 'clipboard', {
    value: { writeText: () => Promise.resolve() },
    configurable: true,
  })
}
