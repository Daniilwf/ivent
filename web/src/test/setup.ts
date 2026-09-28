import '@testing-library/jest-dom/vitest';

// jsdom has no ResizeObserver; the map only needs it to exist (it measures its frame in a browser)
if (!('ResizeObserver' in globalThis)) {
  globalThis.ResizeObserver = class {
    observe() {}
    unobserve() {}
    disconnect() {}
  };
}

// jsdom has no scrolling: a page opened by the router starts at the top, in tests nowhere
window.scrollTo = () => undefined;

// The site's status is asked once per page (siteStatus.ts): every test is a new page
import { forgetSiteEnvironment } from '../app/siteStatus';
beforeEach(() => {
  forgetSiteEnvironment();
});
