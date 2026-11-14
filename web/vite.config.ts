import { defineConfig } from "vitest/config";
import react from "@vitejs/plugin-react";
import tailwindcss from "@tailwindcss/vite";

// Development only: the container serves the built assets from nginx and
// reverse-proxies /auth and /api (see web/nginx/default.conf.template).
// `npm run dev` talks to the host-published auth (8081) and api (8082) ports
// directly; the browser still uses same-origin relative paths in both setups.
export default defineConfig({
  plugins: [react(), tailwindcss()],
  server: {
    port: 5173,
    proxy: {
      "/auth": {
        target: "http://localhost:8081",
        changeOrigin: true,
      },
      "/api": {
        target: "http://localhost:8082",
        changeOrigin: true,
      },
    },
  },
  test: {
    environment: "jsdom",
    setupFiles: ["./src/test/setup.ts"],
    css: true,
    restoreMocks: true,
  },
});
