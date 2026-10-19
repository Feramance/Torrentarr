import { createHash } from "node:crypto";
import { readFileSync, writeFileSync } from "node:fs";
import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import { resolve } from "node:path";

// https://vite.dev/config/
export default defineConfig({
  base: "./",
  plugins: [
    react(),
    {
      name: "torrentarr-service-worker-version",
      closeBundle() {
        const root = resolve(__dirname, "../src/Torrentarr.Host/wwwroot");
        const version = createHash("sha256")
          .update(readFileSync(resolve(root, "index.html")))
          .digest("hex")
          .slice(0, 16);
        const path = resolve(root, "sw.js");
        writeFileSync(
          path,
          readFileSync(path, "utf8").replaceAll(
            "__TORRENTARR_CACHE_VERSION__",
            version,
          ),
        );
      },
    },
  ],
  build: {
    outDir: resolve(__dirname, "../src/Torrentarr.Host/wwwroot"),
    emptyOutDir: true,
    sourcemap: false,
    chunkSizeWarningLimit: 1000,
    rollupOptions: {
      output: {
        manualChunks(id) {
          if (id.includes("@tanstack/react-table")) return "table";
          if (
            id.includes("@mantine/core") ||
            id.includes("@mantine/hooks") ||
            id.includes("@mantine/dates")
          )
            return "mantine";
          if (id.includes("react") || id.includes("react-dom")) return "vendor";
          return undefined;
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
