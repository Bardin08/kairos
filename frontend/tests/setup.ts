import { JSDOM } from 'jsdom'
import { vi } from 'vitest'

// Node 26 declares a global `localStorage` that reads as undefined unless the
// process was started with --localstorage-file. Vitest's jsdom environment only
// copies window properties globalThis does not already have, so that stub keeps
// its place and jsdom's real Storage never lands. Put a real one there.
const { window } = new JSDOM('', { url: 'http://localhost:3000/' })

Object.defineProperty(globalThis, 'localStorage', {
  value: window.localStorage,
  configurable: true,
})

// jsdom 30 ships `<dialog>` as an element and none of its behaviour: reading
// `showModal` gives undefined and calling it throws. Verified against 30.0.1,
// which is what this project pins.
//
// What this shim buys is that the cancel dialog's own logic stays testable —
// what opens it, what closes it, what it renders, what it calls and with which
// argument, and where focus goes. What it deliberately does NOT buy, and what
// no test in this suite may be read as proving:
//
//   - the focus trap. Tab here walks the whole document, as it always did.
//   - the inertness of the page behind a modal.
//   - Escape closing the dialog, which is the UA's behaviour and not React's.
//   - the backdrop, and therefore anything about clicking it.
//
// Those four are pinned in a real browser instead, by the `cancel-dialog` check
// in scripts/measure-ui.mjs. A test here that appears to cover one of them is
// covering this shim.
if (typeof HTMLDialogElement !== 'undefined' && HTMLDialogElement.prototype.showModal === undefined) {
  HTMLDialogElement.prototype.showModal = function showModal(this: HTMLDialogElement) {
    this.open = true
  }
  HTMLDialogElement.prototype.close = function close(this: HTMLDialogElement, returnValue?: string) {
    if (!this.open) return
    this.open = false
    if (returnValue !== undefined) this.returnValue = returnValue
    // The UA fires this, and the component listens to it to return focus, so a
    // shim that stayed silent would make the component look broken.
    this.dispatchEvent(new Event('close'))
  }
}

// jsdom ships no clipboard. The editor's Copy button needs one to exist, and
// `writeText` is a spy rather than a bare stub so a test can assert what was
// written to it.
if (navigator.clipboard === undefined) {
  Object.defineProperty(navigator, 'clipboard', {
    value: { writeText: vi.fn(() => Promise.resolve()) },
    configurable: true,
  })
}
