import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import './design/index.css';
import { App } from './App';
import { StyleguidePage } from './styleguide/StyleguidePage';
import { startBugContext } from './app/bugContext';

const root = document.getElementById('root');
if (!root) throw new Error('Root element #root is missing in index.html');

// The bug report's context is kept from the first moment: the actions before a bug are the useful ones
startBugContext();

// The styleguide (G3) needs no server and no sign-in: every component on demo data
const page = window.location.pathname === '/styleguide' ? <StyleguidePage /> : <App />;

createRoot(root).render(<StrictMode>{page}</StrictMode>);
