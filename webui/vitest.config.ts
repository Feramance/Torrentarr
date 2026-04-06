import { defineConfig } from "vitest/config";
import react from "@vitejs/plugin-react";
import type { Plugin } from "vite";

// Virtual module ID used to stub all *.svg imports in tests.
const SVG_STUB_ID = "\0vitest-svg-stub";

/**
 * Intercepts every `*.svg` import and redirects it to a virtual module that
 * exports the filename as a plain string.  This avoids the need for actual
 * SVG content in jsdom where image rendering is irrelevant.
 */
const svgStubPlugin: Plugin = {
  name: "vitest-svg-stub",
  enforce: "pre",
  resolveId(id) {
    if (/\.svg(\?.*)?$/.test(id)) {
      return SVG_STUB_ID;
    }
  },
  load(id) {
    if (id === SVG_STUB_ID) {
      return 'export default "test-file-stub.svg";';
    }
  },
};

export default defineConfig({
  plugins: [svgStubPlugin, react()],
  test: {
    environment: "jsdom",
    setupFiles: ["./src/__tests__/setup.ts"],
    globals: true,
    // MSW setupServer() is per-file; parallel test files share one worker process and can
    // race handlers → flaky findBy* timeouts. Run files sequentially (tests within a file
    // still run in parallel).
    fileParallelism: false,
    coverage: {
      provider: "v8",
      reporter: ["text", "json", "html"],
      exclude: ["node_modules/**", "src/__tests__/**", "dist/**", "*.config.*"],
    },
  },
});
