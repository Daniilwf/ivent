/// <reference types="vitest/config" />
import { resolve } from 'node:path';
import react from '@vitejs/plugin-react';
import { defineConfig } from 'vite';

export default defineConfig({
  plugins: [react()],
  // G2: the design previews are a page of their own (design-preview.html), apart from the site
  build: {
    rollupOptions: {
      input: {
        main: resolve(import.meta.dirname, 'index.html'),
        preview: resolve(import.meta.dirname, 'design-preview.html'),
      },
    },
  },
  // Development: the backend runs on :5080 (launchSettings), the page talks to it through Vite.
  server: {
    port: 5173,
    proxy: {
      '/api': 'http://localhost:5080',
      '/hubs': { target: 'http://localhost:5080', ws: true },
    },
  },
  test: {
    environment: 'jsdom',
    globals: true,
    setupFiles: ['./src/test/setup.ts'],
    css: false,
  },
});
