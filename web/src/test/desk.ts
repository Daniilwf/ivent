// Tests only: the page as a desktop sees it (useDesk reads the min-width query). Undo with vi.unstubAllGlobals().
export function onDesktop() {
  vi.stubGlobal('matchMedia', (query: string) => ({
    matches: query.includes('min-width'),
    media: query,
    addEventListener: () => undefined,
    removeEventListener: () => undefined,
  }));
}
