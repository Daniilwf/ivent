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
Element.prototype.scrollIntoView = () => undefined;

// Every test is a new visit of the site: the status is asked again (siteStatus.ts), and the first page does not take
// the focus to its heading (router.ts). A link followed in an earlier test of the file would otherwise leave the
// flag set, and a later page's heading would take the focus one frame after it mounted: in jsdom that frame may come
// while the test is already typing into a field (FL-1)
import { forgetSiteEnvironment } from '../app/siteStatus';
import { forgetNavigation } from '../app/router';
import { keepSeason } from '../admin/pickedSeason';
beforeEach(() => {
  forgetSiteEnvironment();
  forgetNavigation();
  keepSeason(null);
});
