// Build of the web application (slice S1.12a). Output goes to dist/ (served by the API host with Web:Root). Nothing is
// fetched from another host at run time (spec 13 FR-UX-PRF-02): fonts and icons are bundled. The component catalogue
// is built only when VITE_ERP_CATALOGUE=true (test and development builds, ruling Q19).
import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

export default defineConfig(({ mode }) => ({
  plugins: [react()],
  build: {
    target: 'es2022',
    outDir: mode === 'e2e' ? 'dist-e2e' : 'dist',
    emptyOutDir: true,
    sourcemap: false,
    assetsInlineLimit: 0,
    chunkSizeWarningLimit: 600,
  },
  server: {
    // Development: the API host runs on 5080; the dev server forwards /api with the browser's host name.
    proxy: { '/api': { target: 'http://127.0.0.1:5080', changeOrigin: false } },
  },
}));
