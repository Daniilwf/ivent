/// <reference types="vitest/config" />
import tailwindcss from '@tailwindcss/vite';
import react from '@vitejs/plugin-react';
import { defineConfig } from 'vite';

export default defineConfig({
  plugins: [react(), tailwindcss()],
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
    // A test's budget, not its expected time: a loaded machine (the .NET suite, Playwright and other worktrees at once)
    // made userEvent-heavy tests cross the default 5 s at random; a real hang still fails, at 15 s
    testTimeout: 15_000,
  },
});
