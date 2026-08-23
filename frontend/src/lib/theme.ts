// Three states, not two. `system` is the default and sets no attribute at all,
// leaving kairos.css's prefers-color-scheme block in charge; an explicit choice
// sets data-theme and wins in both directions.
//
// index.html repeats the read half of this file as an inline script in <head>,
// on purpose and permanently: it has to run before first paint or a stored
// choice flashes the wrong theme for a frame, and importing this module would
// cost a network round trip to avoid a repaint. Change the storage key or the
// attribute here and that script changes too — nothing enforces it but this
// comment.
export type ThemeChoice = 'system' | 'light' | 'dark'

const STORAGE_KEY = 'theme'

// Every localStorage touch is wrapped, because the property access itself throws
// in Safari with "Block All Cookies", in some in-app webviews and in a
// cross-origin iframe — and an in-app webview is exactly where a stranger opens
// a pasted booking link. An uncaught throw here is a white page instead of a
// booking form.

export function readChoice(): ThemeChoice {
  try {
    const stored = localStorage.getItem(STORAGE_KEY)
    if (stored === 'light' || stored === 'dark') return stored
    // A value that is neither would otherwise sit there forever, reread and
    // rejected on every load.
    if (stored !== null) localStorage.removeItem(STORAGE_KEY)
  } catch {
    // No storage available. System default it is.
  }
  return 'system'
}

export function applyChoice(choice: ThemeChoice): void {
  const root = document.documentElement
  if (choice === 'system') {
    // Removing the attribute hands control back to prefers-color-scheme.
    root.removeAttribute('data-theme')
  } else {
    root.setAttribute('data-theme', choice)
  }

  // Painted first, remembered second: a browser that refuses storage still gets
  // the theme it asked for, and only loses it on reload.
  try {
    if (choice === 'system') {
      localStorage.removeItem(STORAGE_KEY)
    } else {
      localStorage.setItem(STORAGE_KEY, choice)
    }
  } catch {
    // The choice holds for this page view and no longer.
  }
}
