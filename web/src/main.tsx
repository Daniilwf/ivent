import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { App } from './App';
import { startBugContext } from './app/bugContext';
import { PreviewA } from './design-preview/PreviewA';
import { PreviewB } from './design-preview/PreviewB';
import { PreviewC } from './design-preview/PreviewC';

const root = document.getElementById('root');
if (!root) throw new Error('Root element #root is missing in index.html');

// The bug report's context is kept from the first moment: the actions before a bug are the useful ones
startBugContext();

// G2: the three directions' main screens, until one is chosen (docs/DESIGN.md «Процесс»)
const preview = /^\/design-preview\/([abc])\/?$/.exec(globalThis.location.pathname)?.[1];
const previews = { a: PreviewA, b: PreviewB, c: PreviewC } as const;
const Screen = preview ? previews[preview as keyof typeof previews] : App;

createRoot(root).render(
  <StrictMode>
    <Screen />
  </StrictMode>,
);
