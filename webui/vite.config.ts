import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import { resolve } from "node:path";

// https://vite.dev/config/
export default defineConfig({
  base: "/",
  plugins: [react()],
  build: {
    outDir: resolve(__dirname, "../src/Torrentarr.Host/wwwroot"),
    emptyOutDir: true,
    sourcemap: false,
    chunkSizeWarningLimit: 1000,
    rollupOptions: {
      output: {
        manualChunks: {
          vendor: ["react", "react-dom"],
          table: ["@tanstack/react-table"],
          mantine: ["@mantine/core", "@mantine/hooks", "@mantine/dates"],
        },
      },
    },
  },
  server: {
    port: 3000,
    proxy: {
      "/api": "http://localhost:6969",
      "/web": "http://localhost:6969",
    },
  },
});
