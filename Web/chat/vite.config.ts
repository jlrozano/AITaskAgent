/// <reference types="vitest/config" />
import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import { resolve } from "node:path";

// Build de biblioteca: dist/index.js y dist/mermaid.js (ES) + dist/chat.css; React queda fuera (peer). El anfitrión puede consumir
// también el código fuente (main = src/index.ts), como hace el cliente web de ejemplo.
export default defineConfig({
  plugins: [react()],
  build: {
    lib: {
      entry: { index: resolve(import.meta.dirname, "src/index.ts"), mermaid: resolve(import.meta.dirname, "src/mermaid.ts") },
      formats: ["es"],
      fileName: (_format, name) => `${name}.js`,
      cssFileName: "chat",
    },
    rollupOptions: { external: ["react", "react-dom", "react/jsx-runtime", "streamdown", "@streamdown/mermaid"] },
  },
  test: { environment: "node", testTimeout: 15000, include: ["tests/**/*.test.{ts,tsx}"] },
});
