import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { App } from './App';
import { startBugContext } from './app/bugContext';

const root = document.getElementById('root');
if (!root) throw new Error('Root element #root is missing in index.html');

// The bug report's context is kept from the first moment: the actions before a bug are the useful ones
startBugContext();

createRoot(root).render(
  <StrictMode>
    <App />
  </StrictMode>,
);
