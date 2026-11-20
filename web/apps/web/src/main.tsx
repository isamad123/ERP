// Entry of the web application (slice S1.12a). Fonts are bundled (spec 13 FR-UX-TOK-10, ruling Q7): Noto Sans for
// English, Noto Naskh Arabic for Urdu; the browser loads only the subsets a page uses (unicode-range).
import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import '@fontsource/noto-sans/400.css';
import '@fontsource/noto-sans/600.css';
import '@fontsource/noto-naskh-arabic/arabic-400.css';
import '@fontsource/noto-naskh-arabic/arabic-600.css';
import '@erp/ui';
import './app/app.css';
import { configureGeneratedClient } from './api/client';
import { App } from './app/App';

configureGeneratedClient();
const root = document.getElementById('root');
if (root) {
  createRoot(root).render(
    <StrictMode>
      <App />
    </StrictMode>,
  );
}
