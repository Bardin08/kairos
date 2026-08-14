import { rmSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import react from '@vitejs/plugin-react'
// vitest/config is Vite's own defineConfig widened to accept `test`, so the
// config typechecks without a triple-slash reference.
import { defineConfig, type Plugin } from 'vitest/config'

// The SPA is built into the API's wwwroot and served from there as static files.
const outDir = '../backend/src/Kairos.Api/wwwroot'
const assetsDir = new URL(`${outDir}/assets/`, import.meta.url)

// Warn about any chunk over 60 kB, the size at which a single chunk is worth
// questioning. The gzipped budget the build is actually held to lives in
// scripts/bundle-budget.sh.
const chunkSizeWarningLimitKb = 60

// Vite only clears an outDir it owns, and it does not own this one — kairos.css
// lives there. Left alone, hashed bundles from every earlier build pile up in
// assets/, and scripts/bundle-budget.sh weighs all of them at once.
function clearStaleAssets(): Plugin {
  return {
    name: 'kairos-clear-stale-assets',
    apply: 'build',
    buildStart() {
      rmSync(fileURLToPath(assetsDir), { recursive: true, force: true })
    },
  }
}

export default defineConfig({
  plugins: [react(), clearStaleAssets()],
  build: {
    outDir,
    emptyOutDir: false, // kairos.css lives there and is not ours to delete
    // public/ holds one file: a symlink to that same kairos.css, so the dev
    // server can serve it at the /kairos.css the API serves in production.
    // Copying it would write the vendored stylesheet over itself.
    copyPublicDir: false,
    chunkSizeWarningLimit: chunkSizeWarningLimitKb,
  },
  server: { proxy: { '/api': 'http://localhost:5000' } },
  test: {
    environment: 'jsdom',
    include: ['tests/**/*.test.{ts,tsx}'],
    setupFiles: ['tests/setup.ts'],
  },
})
