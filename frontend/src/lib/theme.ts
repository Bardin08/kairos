export type ThemeChoice = 'system' | 'light' | 'dark'

const STORAGE_KEY = 'theme'

export function readChoice(): ThemeChoice {
  const stored = localStorage.getItem(STORAGE_KEY)
  return stored === 'light' || stored === 'dark' ? stored : 'system'
}

export function applyChoice(choice: ThemeChoice): void {
  const root = document.documentElement
  if (choice === 'system') {
    // Removing the attribute hands control back to prefers-color-scheme.
    root.removeAttribute('data-theme')
    localStorage.removeItem(STORAGE_KEY)
    return
  }
  root.setAttribute('data-theme', choice)
  localStorage.setItem(STORAGE_KEY, choice)
}
