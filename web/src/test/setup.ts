import '@testing-library/jest-dom/vitest';

// jsdom has no ResizeObserver; the map only needs it to exist (it measures its frame in a browser)
if (!('ResizeObserver' in globalThis)) {
  globalThis.ResizeObserver = class {
    observe() {}
    unobserve() {}
    disconnect() {}
  };
}
