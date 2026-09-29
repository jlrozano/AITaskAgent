import { defineConfig } from 'vitest/config';
import react from '@vitejs/plugin-react';

// Backend de desarrollo: CodebaseAnalysisWeb escucha solo en 127.0.0.1:5180 (plan §8 F2).
const backend = 'http://127.0.0.1:5180';

export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    strictPort: true,
    proxy: {
      '/api': {
        target: backend,
        // Host se reenvía tal cual (localhost:5173): AllowedHosts del backend admite "localhost".
        changeOrigin: false,
        configure: (proxy) => {
          // Si el navegador corta la petición (botón «Parar» del chat, pestaña cerrada), se corta también
          // la petición al backend para que vea RequestAborted y cancele el turno.
          proxy.on('proxyReq', (proxyReq, _req, res) => {
            res.on('close', () => {
              if (!res.writableEnded) proxyReq.destroy();
            });
          });
          // SSE: se envían las cabeceras en cuanto llegan y no se comprime ni agrupa la respuesta.
          proxy.on('proxyRes', (proxyRes, _req, res) => {
            const type = String(proxyRes.headers['content-type'] ?? '');
            if (type.startsWith('text/event-stream')) {
              proxyRes.headers['cache-control'] = 'no-cache';
              proxyRes.headers['x-accel-buffering'] = 'no';
              res.once('pipe', () => res.flushHeaders());
            }
          });
        },
      },
    },
  },
  build: {
    // La SPA se sirve desde el wwwroot del backend (UseStaticFiles + MapFallbackToFile).
    outDir: '../wwwroot',
    emptyOutDir: true,
    chunkSizeWarningLimit: 1500,
  },
  test: {
    environment: 'jsdom',
    setupFiles: './src/test/setup.ts',
    css: false,
    restoreMocks: true,
  },
});
