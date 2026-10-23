import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'
import tailwindcss from '@tailwindcss/vite'
import { fileURLToPath, URL } from 'node:url'

// The base path is the GitHub Pages project URL, which is derived from the repository
// name: https://<owner>.github.io/<repo>/
//
// It has to be read from the environment rather than hard-coded. A project page served
// from a subpath loads its assets from that subpath, so a wrong base gives a blank page
// with 404s on every script - and the previous hard-coded value named a different
// repository, which is exactly that failure. The workflow passes the real name in.
const repoName = process.env.GITHUB_REPOSITORY?.split('/')[1]

export default defineConfig({
  plugins: [vue(), tailwindcss()],
  // Falls back to the local repo name for `npm run dev`, and to '/' for a user/org page.
  base: repoName ? `/${repoName}/` : '/',
  resolve: {
    alias: {
      '@': fileURLToPath(new URL('./src', import.meta.url))
    }
  }
})
