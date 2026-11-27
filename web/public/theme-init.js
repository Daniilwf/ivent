// Applies the stored theme before the first paint (D-940). The site's policy runs only its own script files (SEC-1).
// The key and attribute match src/design/theme.ts. With no stored choice, the canvas follows the system.
(function () {
  var root = document.documentElement;
  var choice;
  try {
    choice = localStorage.getItem('ge.theme');
  } catch {
    // Storage may be unavailable. The page follows the system.
  }
  if (choice === 'light' || choice === 'dark') {
    root.dataset.theme = choice;
    var scheme = document.querySelector('meta[name="color-scheme"]');
    if (scheme) scheme.content = choice;
  }

  var bar = document.querySelector('meta[name="theme-color"]');
  var page = getComputedStyle(root).getPropertyValue('--initial-page-color').trim();
  if (bar && page) bar.content = page;
})();
