import { createReadStream, rmSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import react from '@vitejs/plugin-react'
// vitest/config is Vite's own defineConfig widened to accept `test`, so the
// config typechecks without a triple-slash reference.
import { defineConfig, type Plugin } from 'vitest/config'

// The SPA is built into the API's wwwroot and served from there as static files.
const outDir = '../backend/src/Kairos.Api/wwwroot'
// Resolved here rather than left to Vite: these two need the same directory
// Vite is writing to, so if outDir ever moves, all three move together.
const assetsDir = fileURLToPath(new URL(`${outDir}/assets/`, import.meta.url))
const stylesheet = fileURLToPath(new URL(`${outDir}/kairos.css`, import.meta.url))

const stylesheetUrl = '/kairos.css'

// In production the API serves kairos.css out of wwwroot next to index.html.
// The dev server has to answer the same URL, and it is handed over here rather
// than through public/ so that public/ keeps its ordinary meaning: files put
// there ship. Serving it from a directory the build also writes to would mean
// copying the vendored stylesheet over itself on every build.
function serveVendoredStylesheet(): Plugin {
  return {
    name: 'kairos-serve-vendored-stylesheet',
    apply: 'serve',
    configureServer(server) {
      server.middlewares.use(stylesheetUrl, (_request, response) => {
        response.setHeader('Content-Type', 'text/css')
        createReadStream(stylesheet).pipe(response)
      })
    },
  }
}

// Vite only clears an outDir it owns, and it does not own this one — kairos.css
// lives there. Left alone, hashed bundles from every earlier build pile up in
// assets/, and scripts/bundle-budget.sh weighs all of them at once.
function clearStaleAssets(): Plugin {
  return {
    name: 'kairos-clear-stale-assets',
    apply: 'build',
    buildStart() {
      rmSync(assetsDir, { recursive: true, force: true })
    },
  }
}

export default defineConfig({
  plugins: [react(), serveVendoredStylesheet(), clearStaleAssets()],
  build: {
    outDir,
    emptyOutDir: false, // kairos.css lives there and is not ours to delete
    // Written for scripts/bundle-budget.sh, which has to tell the chunks the
    // booking route loads on first paint from the admin chunks it never fetches.
    // Nothing at runtime reads it — index.html is generated with the hashes
    // baked in — so this is a build artefact, and wwwroot/* is gitignored.
    manifest: true,
  },
  server: { proxy: { '/api': 'http://localhost:5000' } },
  test: {
    environment: 'jsdom',
    include: ['tests/**/*.test.{ts,tsx}'],
    setupFiles: ['tests/setup.ts'],
  },
})
