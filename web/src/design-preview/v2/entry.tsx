import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { Preview } from './Preview';

// The second round of design previews (G2): design-preview.html, apart from the site's own page
const root = document.getElementById('root');
if (root) {
  createRoot(root).render(
    <StrictMode>
      <Preview />
    </StrictMode>,
  );
}
